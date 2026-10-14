using System.Text;

namespace Como;

/// <summary>
/// Pinta la respuesta en streaming: primero el resumen y, tras el marcador, el comando,
/// cada parte con su color. Si el modelo no usa el marcador, todo se trata como resumen.
/// </summary>
public sealed class ResponseRenderer
{
    public const string Marker = "@@CMD@@";

    private const ConsoleColor SummaryColor = ConsoleColor.Yellow;
    private const ConsoleColor CommandColor = ConsoleColor.Green;

    private readonly StringBuilder _buf = new();
    private bool _inCommand;
    private bool _skipLeading;
    private bool _wroteAny;

    public void Add(string chunk)
    {
        _buf.Append(chunk);
        if (_inCommand) FlushCommandLines(final: false);
        else FlushSummary(final: false);
    }

    public void Complete()
    {
        if (_inCommand) FlushCommandLines(final: true);
        else FlushSummary(final: true);
        if (_wroteAny) Console.WriteLine();
    }

    private void FlushSummary(bool final)
    {
        var s = _buf.ToString();
        var idx = s.IndexOf(Marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var summary = s[..idx].TrimEnd();
            if (summary.Length > 0)
            {
                Emit(summary, SummaryColor);
                Console.WriteLine();
                Console.WriteLine();
            }
            _buf.Clear().Append(s[(idx + Marker.Length)..]);
            _inCommand = true;
            _skipLeading = true;
            FlushCommandLines(final);
            return;
        }

        // Retiene la cola que podría ser el inicio del marcador partido entre fragmentos.
        var keep = final ? 0 : PartialMarkerLength(s);
        var emit = s.Length - keep;
        if (emit > 0)
        {
            var text = s[..emit];
            if (final) text = text.TrimEnd();
            if (text.Length > 0) Emit(text, SummaryColor);
            _buf.Clear().Append(s[emit..]);
        }
    }

    private void FlushCommandLines(bool final)
    {
        var s = _buf.ToString();
        if (_skipLeading)
        {
            s = s.TrimStart('\r', '\n', ' ');
            if (s.Length == 0 && !final) { _buf.Clear(); return; }
            _skipLeading = false;
        }

        int start = 0, nl;
        while ((nl = s.IndexOf('\n', start)) >= 0)
        {
            WriteCommandLine(s[start..nl].TrimEnd('\r'), newline: true);
            start = nl + 1;
        }

        var rest = s[start..];
        if (final) WriteCommandLine(rest.TrimEnd(), newline: false);
        else _buf.Clear().Append(rest);
        if (final) _buf.Clear();
    }

    private void WriteCommandLine(string line, bool newline)
    {
        // Descarta vallas de markdown que el modelo pueda añadir.
        if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) return;
        if (line.Length == 0 && !newline) return;
        Emit(line, CommandColor);
        if (newline) Console.WriteLine();
    }

    private void Emit(string text, ConsoleColor color)
    {
        ConsoleUi.Write(text, color);
        _wroteAny = true;
    }

    private static int PartialMarkerLength(string s)
    {
        var max = Math.Min(Marker.Length - 1, s.Length);
        for (var len = max; len > 0; len--)
            if (s.EndsWith(Marker[..len], StringComparison.Ordinal)) return len;
        return 0;
    }
}
