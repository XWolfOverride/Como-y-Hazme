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

    Durante el trabajo:
      Esc                         Aborta el trabajo (también durante el tiempo de gracia)
      escribir + Enter            Envía una nota a la IA para el siguiente paso
      Ctrl-C                      Interrumpe el comando en curso (dos veces: lo mata)
      A / D / I                   En un diff: Aplicar, Descartar o dar Instrucciones

    Claves de hazme:
      grace-seconds               Segundos de espera visible antes de ejecutar (por defecto 3)
      max-steps                   Máximo de pasos por trabajo (por defecto 30)
      shell                       Shell a usar (por defecto: bash/sh, PowerShell en Windows)
      confirm-changes             true = confirmar con diff toda escritura de archivos (por defecto false)
    """;

Console.OutputEncoding = new UTF8Encoding(false);

using var job = new CancellationTokenSource();
var shell = default(ShellRunner);

try
{
    // -confirm es propio de hazme y puede ir en cualquier posición.
    var confirmFlag = args.Contains("-confirm");
    if (!CliCommon.TryParse(args.Where(a => a != "-confirm").ToArray(), out var options)) return 1;

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

    if (interaction.Interactive && !Console.IsOutputRedirected)
        ConsoleUi.WriteLine("Esc: abortar · escribe + Enter: nota para la IA · Ctrl-C: interrumpir comando", ConsoleColor.DarkGray);

    interaction.Start();
    try
    {
        // El nombre del programa ("hazme") es la primera palabra de la petición: se restaura.
        var task = "hazme " + string.Join(' ', options.Command);
        var confirmAll = confirmFlag || KnownKeys.IsTrue(KnownKeys.Value(store, HazmeKeys.ConfirmChanges));
        var chat = Hazme.Chat.ChatClients.For(connector, store);
        return await new JobRunner(chat, store, interaction, shell, confirmAll, job.Token).RunAsync(task);
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

