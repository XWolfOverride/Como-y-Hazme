using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Como;
using Como.Settings;

namespace Hazme;

internal sealed class JobPlan
{
    public string Summary { get; set; } = "";
    public List<string> Steps { get; set; } = [];
    public string? Risks { get; set; }
    /// <summary>pendiente, aprobado, modificado o cancelado.</summary>
    public string Status { get; set; } = "pendiente";
}

/// <summary>
/// Lo mínimo para retomar un trabajo interrumpido: la tarea y un diario breve (notas de la IA, decisiones,
/// comandos y cambios hechos). Nunca contiene contenido de archivos ni salida de comandos.
/// </summary>
internal sealed class JobRecord
{
    public string Id { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public string Cwd { get; set; } = "";
    public string Connector { get; set; } = "";
    public string Task { get; set; } = "";
    public List<string> Log { get; set; } = [];
    public JobPlan? Plan { get; set; }
}

[JsonSerializable(typeof(JobRecord))]
internal partial class JobJsonContext : JsonSerializerContext;

/// <summary>Archivos de trabajos en ~/.como/jobs (cifrados, de vida corta).</summary>
internal static partial class JobFiles
{
    private static readonly byte[] Magic = "CJB1"u8.ToArray();

    [GeneratedRegex("^[0-9a-f]{6}$")]
    private static partial Regex IdPattern();

    public static string Dir => Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath())!, "jobs");

    private static string PathOf(string id) => Path.Combine(Dir, id + ".job");

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    public static JobRecord? Load(string id)
    {
        // El id forma parte de una ruta: solo se aceptan los que genera NewId.
        if (!IdPattern().IsMatch(id)) return null;
        try
        {
            var path = PathOf(id);
            if (!File.Exists(path)) return null;
            var plain = EncryptedFile.Open(File.ReadAllBytes(path), Magic);
            return JsonSerializer.Deserialize(plain, JobJsonContext.Default.JobRecord);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                      or CryptographicException or JsonException)
        {
            return null; // ilegible o de otro usuario: no se puede retomar
        }
    }

    public static void Save(JobRecord rec)
    {
        rec.Updated = DateTime.UtcNow;
        var plain = JsonSerializer.SerializeToUtf8Bytes(rec, JobJsonContext.Default.JobRecord);
        EncryptedFile.WriteAtomic(PathOf(rec.Id), EncryptedFile.Seal(plain, Magic));
    }

    public static void Delete(string id)
    {
        if (!IdPattern().IsMatch(id)) return;
        try { File.Delete(PathOf(id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Trabajos pendientes, el más reciente primero.</summary>
    public static List<JobRecord> List()
    {
        var jobs = new List<JobRecord>();
        if (!Directory.Exists(Dir)) return jobs;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "*.job"))
                if (Load(Path.GetFileNameWithoutExtension(f)) is { } rec) jobs.Add(rec);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return jobs.OrderByDescending(j => j.Updated).ToList();
    }

    /// <summary>Borra los trabajos más viejos que el plazo (y los archivos ilegibles o huérfanos).</summary>
    public static void Purge(int retentionHours)
    {
        if (!Directory.Exists(Dir)) return;
        var limit = DateTime.UtcNow.AddHours(-retentionHours);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir))
            {
                var rec = f.EndsWith(".job", StringComparison.Ordinal) ? Load(Path.GetFileNameWithoutExtension(f)) : null;
                if (rec is null || rec.Updated < limit)
                    try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Elige el trabajo de "-resume [id] [instrucciones...]": el id si el primer argumento lo es;
    /// si no, el más reciente (y todo lo escrito son instrucciones).
    /// </summary>
    public static bool TryPick(string[] rest, out JobRecord? rec, out string? extra)
    {
        rec = rest.Length > 0 ? Load(rest[0]) : null;
        if (rec is not null)
            rest = rest[1..];
        else
            rec = List().FirstOrDefault();
        extra = rest.Length > 0 ? string.Join(' ', rest) : null;
        return rec is not null;
    }

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 1 ? "hace un momento"
            : d.TotalHours < 1 ? $"hace {(int)d.TotalMinutes} min"
            : $"hace {(int)d.TotalHours} h {d.Minutes} min";
    }
}

/// <summary>
/// Diario del trabajo en curso. Cada anotación se guarda cifrada al instante (un corte puede llegar en
/// cualquier momento). Si la persistencia está desactivada, no hace nada. Tras <see cref="Finish"/> borra el
/// archivo y no vuelve a escribir.
/// </summary>
internal sealed class JobLog
{
    private const int MaxEntries = 150;
    private const int MaxEntryChars = 400;

    private readonly JobRecord? _rec;
    private bool _finished;
    private bool _warned;

    private JobLog(JobRecord? rec) => _rec = rec;

    public static JobLog Disabled { get; } = new(null);

    public static JobLog Create(string task, string connector)
    {
        var rec = new JobRecord
        {
            Id = JobFiles.NewId(),
            Created = DateTime.UtcNow,
            Cwd = Environment.CurrentDirectory,
            Connector = connector,
            Task = task,
        };
        var log = new JobLog(rec);
        log.Save();
        return log;
    }

    public static JobLog Resume(JobRecord rec) => new(rec);

    public string? Id => _rec?.Id;

    /// <summary>Hay un trabajo guardado que se puede retomar.</summary>
    public bool Resumable => _rec is not null && !_finished;

    public void Note(string kind, string? text)
    {
        if (_rec is null || _finished || string.IsNullOrWhiteSpace(text)) return;
        var line = text.ReplaceLineEndings(" ").Trim();
        if (line.Length > MaxEntryChars) line = line[..MaxEntryChars] + "…";
        _rec.Log.Add($"[{kind}] {line}");
        if (_rec.Log.Count > MaxEntries) _rec.Log.RemoveRange(0, _rec.Log.Count - MaxEntries);
        Save();
    }

    public void SetPlan(string? summary, List<string> steps, string? risks)
    {
        if (_rec is null || _finished) return;
        _rec.Plan = new JobPlan { Summary = summary ?? "", Steps = steps, Risks = risks };
        Save();
    }

    public void SetPlanStatus(string status)
    {
        if (_rec?.Plan is null || _finished) return;
        _rec.Plan.Status = status;
        Save();
    }

    /// <summary>El trabajo ha terminado: ya no hay nada que retomar y se borra todo rastro.</summary>
    public void Finish()
    {
        if (_rec is null) return;
        _finished = true;
        JobFiles.Delete(_rec.Id);
    }

    public void PrintResumeHint(int retentionHours)
    {
        if (!Resumable) return;
        ConsoleUi.WriteLine(
            $"↺ Trabajo guardado ({_rec!.Id}). Para retomarlo: hazme -resume   (se borra solo a las {retentionHours} h)",
            ConsoleColor.Cyan);
    }

    /// <summary>Primer mensaje al retomar: la IA no conserva la conversación, solo este resumen.</summary>
    public string BuildResumePrompt(string? extraInstructions)
    {
        var rec = _rec!;
        var sb = new StringBuilder();
        sb.AppendLine("[Trabajo retomado] Una ejecución anterior de hazme se interrumpió. No conservas aquella conversación: solo este registro.");
        sb.AppendLine("El contenido de los archivos leídos NO se conserva; vuelve a leer lo que necesites.");
        sb.AppendLine();
        sb.AppendLine("Tarea original: " + rec.Task);
        sb.AppendLine("Directorio de trabajo original: " + rec.Cwd);
        if (rec.Log.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Registro de lo ocurrido (más antiguo primero):");
            foreach (var l in rec.Log) sb.AppendLine("- " + l);
        }
        if (rec.Plan is { } p)
        {
            sb.AppendLine();
            sb.AppendLine($"Último plan presentado (estado: {p.Status}): {p.Summary}");
            for (var i = 0; i < p.Steps.Count; i++) sb.AppendLine($"  {i + 1}. {p.Steps[i]}");
            if (!string.IsNullOrWhiteSpace(p.Risks)) sb.AppendLine("  Riesgos: " + p.Risks);
        }
        sb.AppendLine();
        sb.AppendLine(
            "El equipo pudo cambiar desde la interrupción y el último comando pudo ejecutarse solo en parte. " +
            "Antes de seguir, comprueba el estado real con herramientas de solo lectura. " +
            "Ningún plan está aprobado en esta sesión: si quedan cambios por hacer, presenta un plan solo con lo que falta.");
        if (!string.IsNullOrWhiteSpace(extraInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("Instrucciones del usuario al retomar: " + extraInstructions);
        }
        return sb.ToString();
    }

    private void Save()
    {
        try
        {
            JobFiles.Save(_rec!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Que no se pueda guardar el diario no debe romper el trabajo; se avisa una vez.
            if (_warned) return;
            _warned = true;
            ConsoleUi.WriteLine("⚠ No se pudo guardar el diario del trabajo; no podrá retomarse si se interrumpe.",
                ConsoleColor.Yellow, error: true);
        }
    }
}
