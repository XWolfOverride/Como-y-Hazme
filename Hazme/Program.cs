using System.Text;
using Como;
using Como.Connectors;
using Como.Settings;
using Hazme;

const string Usage = """
    Uso:
      hazme                       Sin tarea (con terminal): pregunta qué hacer
      hazme <tarea...>            La IA realiza la tarea ejecutando comandos en este equipo
      hazme -get [clave]          Muestra claves y valores (-get <clave> solo el valor)
      hazme -set <clave> <valor>  Establece el valor de una clave
      hazme -connectors           Lista los conectores implementados
      hazme -models               Lista los modelos disponibles del conector
      hazme -jobs                 Lista los trabajos interrumpidos que se pueden retomar
      hazme -resume [id] [texto]  Retoma un trabajo (el más reciente por defecto), con instrucciones opcionales
      hazme -discard <id|all>     Borra trabajos interrumpidos
      hazme -agent [opciones]     Ejecutor remoto TEMPORAL para el hazme de otra máquina (una sola conexión).
                                  Opciones: -port N (7700) -listen IP (127.0.0.1) -wait MIN (10) -idle MIN (30) -ttl MIN (120)

    Opciones (en cualquier posición):
      -no-color                   Desactiva los colores en esta llamada
      -connector <nombre>         Usa un conector distinto solo en esta llamada
      -confirm                    Confirma con diff TODA escritura de archivos en esta llamada
      -no-plan                    Sin plan previo: la IA actúa directamente en esta llamada
      -less-safe                  MODO MENOS SEGURO en esta llamada: lee archivos privados sin preguntar
      -peer id:host:puerto        Trabaja también en otra máquina con un agente en marcha (repetible); pide su token
      -no-persist                 No guarda el diario del trabajo en esta llamada (no podrá retomarse)
      -y                          Desatendido: no pide confirmaciones (plan, diffs); mantiene los
                                  tiempos de gracia de los comandos y no deja la conversación abierta

    Flujo por defecto: analiza (solo lectura) → muestra un plan → tú apruebas → ejecuta.
    La conversación sigue abierta al terminar: escribe otra instrucción (Enter o Esc para salir).

    Durante el trabajo:
      Esc                         Aborta el trabajo (también durante el tiempo de gracia)
      escribir + Enter            Envía una nota a la IA para el siguiente paso
      Ctrl-C                      Interrumpe el comando en curso (dos veces: lo mata)
      A / M / C                   En un plan: Aprobar, Modificar o Cancelar
      A / D / I                   En un diff: Aplicar, Descartar o dar Instrucciones

    Claves de hazme:
      plan                        false = sin plan previo (por defecto true)
      less-safe                   true = MODO MENOS SEGURO: lee archivos privados sin preguntar (defecto false)
      grace-seconds               Segundos de espera visible antes de ejecutar (por defecto 5)
      max-steps                   Máximo de pasos por trabajo (por defecto 30)
      shell                       Shell a usar (por defecto: bash/sh, PowerShell en Windows)
      confirm-changes             true = confirmar con diff toda escritura de archivos (por defecto false)
      persist-jobs                false = no guardar nunca el diario de trabajos (por defecto true)
      job-retention-hours         Horas que se conserva un trabajo interrumpido (por defecto 4)

    Instrucciones permanentes: escribe lo que la IA debe tener siempre en cuenta en ~/.como/AGENTS.md
    y/o ~/AGENTS.md.
    """;

Console.OutputEncoding = new UTF8Encoding(false);

using var job = new CancellationTokenSource();
var shell = default(ShellRunner);
using var peers = new Hazme.Peer.PeerSet();
JobLog? log = null;
var retentionHours = 4;

try
{
    // -confirm, -no-plan, -y y -less-safe son propios de hazme y pueden ir en cualquier posición.
    var confirmFlag = args.Contains("-confirm");
    var noPlanFlag = args.Contains("-no-plan");
    var yesFlag = args.Contains("-y");
    var lessSafeFlag = args.Contains("-less-safe");
    var noPersistFlag = args.Contains("-no-persist");

    // -peer id:host:puerto (repetible) también puede ir en cualquier posición.
    var peerSpecs = new List<string>();
    var rest = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] != "-peer")
        {
            rest.Add(args[i]);
            continue;
        }
        if (i + 1 >= args.Length)
        {
            ConsoleUi.Error("Uso: -peer id:host:puerto");
            return 1;
        }
        peerSpecs.Add(args[++i]);
    }

    if (!CliCommon.TryParse(
            rest.Where(a => a is not ("-confirm" or "-no-plan" or "-y" or "-less-safe" or "-no-persist")).ToArray(),
            out var options))
        return 1;
    var verb = options.Command.FirstOrDefault();

    // El agente remoto no usa IA ni configuración: arranca y se queda esperando una conexión.
    if (verb == "-agent")
    {
        ConsoleUi.NoColor = options.NoColor;
        return await Hazme.Peer.AgentServer.RunAsync(options.Command);
    }

    // Sin tarea y con terminal, hazme pide la primera tarea; sin terminal no hay a quién preguntar.
    var askTask = options.Command.Length == 0 && !Console.IsInputRedirected;
    if (options.Command.Length == 0 && !askTask)
    {
        Console.WriteLine(Usage);
        return 1;
    }
    if (!askTask && options.Command[0] is "-h" or "--help" or "-help")
    {
        Console.WriteLine(Usage);
        return 0;
    }

    KnownKeys.Register(HazmeKeys.All);
    var store = new SettingsStore();
    ConsoleUi.NoColor = options.NoColor || KnownKeys.IsTrue(store.Get(KnownKeys.NoColor));

    retentionHours = Math.Max(1, KnownKeys.IntValue(store, HazmeKeys.JobRetentionHours, 4));
    JobFiles.Purge(retentionHours);

    if (await CliCommon.TryRunAsync(options, store) is { } code) return code;
    if (verb == "-jobs") return ListJobs(retentionHours);
    if (verb == "-discard") return DiscardJobs(options.Command);

    var connector = CliCommon.ResolveConnector(store, options.Connector);
    shell = new ShellRunner(store);
    var interaction = new Interaction(job);

    // Ctrl-C: si hay un comando en curso es para él; si no, aborta el trabajo.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        if (!shell.HandleCtrlC() && !peers.HandleCtrlC()) job.Cancel();
    };

    // Los riesgos de hazme se aceptan una vez, de forma explícita, antes de su primer uso.
    if (!AcknowledgeRisks(store, interaction)) return 1;

    var lessSafe = lessSafeFlag || KnownKeys.IsTrue(KnownKeys.Value(store, HazmeKeys.LessSafe));
    if (lessSafe)
        ConsoleUi.WriteLine(
            "⚠ MODO MENOS SEGURO: la IA leerá archivos privados sin pedirte permiso. La responsabilidad es tuya.",
            ConsoleColor.Red, error: true);

    // Aviso en cada ejecución si el modelo parece pequeño: ahí es donde más se equivoca hazme.
    var model = connector.Keys.FirstOrDefault(k => k.Name == connector.ModelKeyName)?.Resolve(store, connector.Id);
    if (FileTools.LooksSmallModel(model))
        ConsoleUi.WriteLine(
            $"⚠ «{model}» parece un modelo pequeño. Con hazme puede cometer errores GRAVES (borrar o sobrescribir lo que no debe). " +
            "Revisa cada plan y cada comando, o usa un modelo de primera línea (o `como`).",
            ConsoleColor.Yellow, error: true);

    if (interaction.Interactive && !Console.IsOutputRedirected)
        ConsoleUi.WriteLine("Esc: abortar · escribe + Enter: nota para la IA · Ctrl-C: interrumpir comando", ConsoleColor.DarkGray);

    // Peers: el token se pide por teclado (o en HAZME_PEER_TOKEN_<ID> sin terminal) y nunca se guarda.
    foreach (var text in peerSpecs)
    {
        var spec = Hazme.Peer.PeerSpec.Parse(text);
        if (peers.Has(spec.Id))
        {
            ConsoleUi.Error($"El peer «{spec.Id}» está repetido.");
            return 1;
        }
        var token = ReadPeerToken(spec, interaction);
        if (token is null)
        {
            ConsoleUi.Error($"Token de «{spec.Id}» ausente o con formato no válido.");
            return 1;
        }
        ConsoleUi.Write($"Conectando con {spec.Id} ({spec.Host}:{spec.Port})… ", ConsoleColor.Gray);
        var peer = await Hazme.Peer.PeerClient.ConnectAsync(spec, token, job.Token);
        peers.Add(peer);
        ConsoleUi.WriteLine($"✔ {peer.Info.Machine} · {peer.Info.Os} · usuario {peer.Info.User}", ConsoleColor.Green);
        if (peer.Info.IsRoot)
            ConsoleUi.WriteLine($"  ⚠ «{spec.Id}» corre como root: la IA tendrá permisos de root allí.", ConsoleColor.Red, error: true);
    }

    var persist = !noPersistFlag && KnownKeys.Value(store, HazmeKeys.PersistJobs) != "false";
    var command = options.Command;
    JobRecord? resumed = null;
    string? extra = null;

    if (verb == "-resume")
    {
        if (!JobFiles.TryPick(command[1..], out resumed, out extra))
        {
            ConsoleUi.Error($"No hay ningún trabajo que retomar (se borran a las {retentionHours} h). Mira: hazme -jobs");
            return 1;
        }
    }
    else if (askTask)
    {
        if (JobFiles.List() is { Count: > 0 } pending)
        {
            var p = pending[0];
            ConsoleUi.Write($"Hay un trabajo interrumpido {JobFiles.Ago(p.Updated)}: ", ConsoleColor.Yellow);
            ConsoleUi.WriteLine(Short(p.Task), ConsoleColor.White);
            ConsoleUi.Write("[R]", ConsoleColor.Green);
            ConsoleUi.Write("etomar  ", ConsoleColor.Gray);
            ConsoleUi.Write("[D]", ConsoleColor.Red);
            ConsoleUi.Write("escartar  ", ConsoleColor.Gray);
            ConsoleUi.Write("[N]", ConsoleColor.Cyan);
            ConsoleUi.WriteLine("ueva tarea", ConsoleColor.Gray);

            switch (interaction.ReadChoice("rdn"))
            {
                case 'r': resumed = p; break;
                case 'd': JobFiles.Delete(p.Id); break;
                case null: return 0;
            }
        }

        if (resumed is null)
        {
            ConsoleUi.WriteLine("¿Qué quieres que haga? (Enter o Esc para salir; `hazme -h` muestra la ayuda)", ConsoleColor.DarkGray);
            ConsoleUi.Write("› ", ConsoleColor.White);
            var first = interaction.ReadLine(escCancelsJob: false);
            if (string.IsNullOrWhiteSpace(first)) return 0;
            command = [first.Trim()];
        }
    }

    interaction.Start();
    try
    {
        // El nombre del programa ("hazme") es la primera palabra de la petición: se restaura.
        string task;
        string? firstPrompt = null;
        if (resumed is null)
        {
            task = "hazme " + string.Join(' ', command);
            log = persist ? JobLog.Create(task, connector.Id) : JobLog.Disabled;
        }
        else
        {
            task = resumed.Task;
            ConsoleUi.Write($"↺ Retomando el trabajo {resumed.Id}: ", ConsoleColor.Cyan);
            ConsoleUi.WriteLine(Short(task), ConsoleColor.White);
            if (Directory.Exists(resumed.Cwd))
                Directory.SetCurrentDirectory(resumed.Cwd);
            else
                ConsoleUi.WriteLine($"  ⚠ el directorio original ya no existe: {resumed.Cwd}", ConsoleColor.Yellow, error: true);

            log = JobLog.Resume(resumed);
            firstPrompt = log.BuildResumePrompt(extra);
            log.Note("usuario", extra is null ? "retoma el trabajo" : "retoma el trabajo: " + extra);
            if (!persist) log.Finish(); // sin persistencia no debe quedar ningún rastro
        }
        var confirmAll = confirmFlag || KnownKeys.IsTrue(KnownKeys.Value(store, HazmeKeys.ConfirmChanges));
        var chat = Hazme.Chat.ChatClients.For(connector, store);
        var planRequired = !noPlanFlag && KnownKeys.Value(store, HazmeKeys.Plan) != "false";

        return await new JobRunner(chat, store, interaction, shell, peers, confirmAll, planRequired, yesFlag, lessSafe, log, job.Token)
            .RunAsync(task, firstPrompt);
    }
    finally
    {
        interaction.Stop();
    }
}
catch (Exception e) when (e is SettingsException or ConnectorException or Hazme.Peer.PeerException)
{
    ConsoleUi.Error(e.Message);
    log?.PrintResumeHint(retentionHours);
    return 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    ConsoleUi.WriteLine("✖ Trabajo abortado.", ConsoleColor.Red);
    log?.PrintResumeHint(retentionHours);
    return 130;
}

static string? ReadPeerToken(Hazme.Peer.PeerSpec spec, Interaction interaction)
{
    var raw = Environment.GetEnvironmentVariable("HAZME_PEER_TOKEN_" + spec.Id.ToUpperInvariant().Replace('-', '_'));
    if (raw is null)
    {
        if (!interaction.Interactive) return null;
        ConsoleUi.Write($"Token de «{spec.Id}» ({spec.Host}:{spec.Port}) › ", ConsoleColor.White);
        raw = interaction.ReadLine(escCancelsJob: false, mask: true);
    }
    return Hazme.Peer.PeerToken.Normalize(raw);
}

static string Short(string text)
{
    text = text.ReplaceLineEndings(" ");
    return text.Length > 80 ? text[..80] + "…" : text;
}

static int ListJobs(int retentionHours)
{
    var jobs = JobFiles.List();
    if (jobs.Count == 0)
    {
        Console.WriteLine("No hay trabajos pendientes.");
        return 0;
    }
    foreach (var j in jobs)
    {
        ConsoleUi.Write(j.Id, ConsoleColor.Cyan);
        ConsoleUi.Write("  " + JobFiles.Ago(j.Updated).PadRight(18), ConsoleColor.DarkGray);
        ConsoleUi.WriteLine(Short(j.Task), ConsoleColor.White);
    }
    ConsoleUi.WriteLine($"Retomar: hazme -resume [id]   ·   Borrar: hazme -discard <id|all>   (se borran a las {retentionHours} h)",
        ConsoleColor.DarkGray);
    return 0;
}

static int DiscardJobs(string[] command)
{
    if (command.Length != 2)
    {
        ConsoleUi.Error("Uso: -discard <id|all>");
        return 1;
    }
    if (command[1] == "all")
    {
        var all = JobFiles.List();
        foreach (var j in all) JobFiles.Delete(j.Id);
        Console.WriteLine($"Borrados {all.Count} trabajos.");
        return 0;
    }
    if (JobFiles.Load(command[1]) is null)
    {
        ConsoleUi.Error($"No existe el trabajo {command[1]}.");
        return 1;
    }
    JobFiles.Delete(command[1]);
    Console.WriteLine("Borrado.");
    return 0;
}


/// <summary>
/// Primer uso: muestra los riesgos y exige escribir "entiendo". Se guarda en la configuración (risk-ack).
/// Sin terminal no se puede preguntar: hay que aceptarlo antes (interactivamente o con -set risk-ack true).
/// </summary>
static bool AcknowledgeRisks(SettingsStore store, Interaction interaction)
{
    if (KnownKeys.IsTrue(KnownKeys.Value(store, HazmeKeys.RiskAck))) return true;

    Console.WriteLine();
    ConsoleUi.WriteLine("ANTES DE USAR hazme", ConsoleColor.Red);
    ConsoleUi.WriteLine("hazme deja que una IA ejecute comandos y escriba archivos en este equipo con TUS permisos.", ConsoleColor.White);
    ConsoleUi.WriteLine("  • Puede equivocarse, y lo ya ejecutado NO se deshace: Esc y el tiempo de gracia solo", ConsoleColor.Yellow);
    ConsoleUi.WriteLine("    evitan lo que aún no ha empezado.", ConsoleColor.Yellow);
    ConsoleUi.WriteLine("  • Con modelos pequeños, gratuitos o locales el riesgo es MUCHO mayor: pueden borrar o", ConsoleColor.Yellow);
    ConsoleUi.WriteLine("    sobrescribir lo que no debían.", ConsoleColor.Yellow);
    ConsoleUi.WriteLine("  • Usa un modelo de primera línea, revisa cada plan y cada comando, y ten copias de seguridad.", ConsoleColor.Yellow);
    ConsoleUi.WriteLine("  • El contenido de los archivos que lee y la salida de los comandos se envían al servicio de IA.", ConsoleColor.Yellow);
    Console.WriteLine();

    if (!interaction.Interactive)
    {
        ConsoleUi.Error("Para usar hazme sin terminal, acepta antes los riesgos: hazme -set risk-ack true");
        return false;
    }

    ConsoleUi.Write("Escribe «entiendo» para continuar: ", ConsoleColor.White);
    var answer = interaction.ReadLine(escCancelsJob: false);
    if (!string.Equals(answer?.Trim(), "entiendo", StringComparison.OrdinalIgnoreCase))
    {
        ConsoleUi.Error("No aceptado: hazme no se ejecuta.");
        return false;
    }

    store.Set(HazmeKeys.RiskAck, "true");
    ConsoleUi.WriteLine("✔ Aceptado (se recuerda; puedes revisarlo con `hazme -get risk-ack`).", ConsoleColor.Green);
    Console.WriteLine();
    return true;
}
