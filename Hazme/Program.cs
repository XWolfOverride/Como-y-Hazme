using System.Text;
using Como;
using Como.Connectors;
using Como.Settings;
using Hazme;

const string Usage = """
    Uso:
      hazme <tarea...>            La IA realiza la tarea ejecutando comandos en este equipo
      hazme -get [clave]          Muestra claves y valores (-get <clave> solo el valor)
      hazme -set <clave> <valor>  Establece el valor de una clave
      hazme -connectors           Lista los conectores implementados
      hazme -models               Lista los modelos disponibles del conector

    Opciones (en cualquier posición):
      -no-color                   Desactiva los colores en esta llamada
      -connector <nombre>         Usa un conector distinto solo en esta llamada
      -confirm                    Confirma con diff TODA escritura de archivos en esta llamada
      -no-plan                    Sin plan previo: la IA actúa directamente en esta llamada
      -less-safe                  MODO MENOS SEGURO en esta llamada: lee archivos privados sin preguntar
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
    """;

Console.OutputEncoding = new UTF8Encoding(false);

using var job = new CancellationTokenSource();
var shell = default(ShellRunner);

try
{
    // -confirm, -no-plan, -y y -less-safe son propios de hazme y pueden ir en cualquier posición.
    var confirmFlag = args.Contains("-confirm");
    var noPlanFlag = args.Contains("-no-plan");
    var yesFlag = args.Contains("-y");
    var lessSafeFlag = args.Contains("-less-safe");
    if (!CliCommon.TryParse(
            args.Where(a => a is not ("-confirm" or "-no-plan" or "-y" or "-less-safe")).ToArray(), out var options))
        return 1;

    if (options.Command.Length == 0)
    {
        Console.WriteLine(Usage);
        return 1;
    }
    if (options.Command[0] is "-h" or "--help" or "-help")
    {
        Console.WriteLine(Usage);
        return 0;
    }

    KnownKeys.Register(HazmeKeys.All);
    var store = new SettingsStore();
    ConsoleUi.NoColor = options.NoColor || KnownKeys.IsTrue(store.Get(KnownKeys.NoColor));

    if (await CliCommon.TryRunAsync(options, store) is { } code) return code;

    var connector = CliCommon.ResolveConnector(store, options.Connector);
    shell = new ShellRunner(store);
    var interaction = new Interaction(job);

    // Ctrl-C: si hay un comando en curso es para él; si no, aborta el trabajo.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        if (!shell.HandleCtrlC()) job.Cancel();
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

    interaction.Start();
    try
    {
        // El nombre del programa ("hazme") es la primera palabra de la petición: se restaura.
        var task = "hazme " + string.Join(' ', options.Command);
        var confirmAll = confirmFlag || KnownKeys.IsTrue(KnownKeys.Value(store, HazmeKeys.ConfirmChanges));
        var chat = Hazme.Chat.ChatClients.For(connector, store);
        var planRequired = !noPlanFlag && KnownKeys.Value(store, HazmeKeys.Plan) != "false";

        return await new JobRunner(chat, store, interaction, shell, confirmAll, planRequired, yesFlag, lessSafe, job.Token)
            .RunAsync(task);
    }
    finally
    {
        interaction.Stop();
    }
}
catch (Exception e) when (e is SettingsException or ConnectorException)
{
    ConsoleUi.Error(e.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    ConsoleUi.WriteLine("✖ Trabajo abortado.", ConsoleColor.Red);
    return 130;
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
