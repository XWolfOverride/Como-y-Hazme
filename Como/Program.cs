using System.Runtime.InteropServices;
using System.Text;
using Como;
using Como.Providers;
using Como.Settings;

const string Usage = """
    Uso:
      como <pregunta...>          Pregunta a la IA cómo hacer algo en la línea de comandos
      como -get                   Lista todas las claves y sus valores
      como -get <clave>           Muestra solo el valor de una clave
      como -set <clave> <valor>   Establece el valor de una clave
      como -providers             Lista los proveedores implementados
      como -models                Lista los modelos disponibles del proveedor

    Opciones (en cualquier posición):
      -no-color                   Desactiva los colores en esta llamada
      -provider <nombre>          Usa un proveedor distinto solo en esta llamada
    """;

Console.OutputEncoding = new UTF8Encoding(false);

try
{
    if (!CliCommon.TryParse(args, out var options)) return 1;

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

    var store = new SettingsStore();
    ConsoleUi.NoColor = options.NoColor || KnownKeys.IsTrue(store.Get(KnownKeys.NoColor));

    if (await CliCommon.TryRunAsync(options, store) is { } code) return code;

    return await AskAsync(store, options.Provider, string.Join(' ', options.Command));
}
catch (Exception e) when (e is SettingsException or ProviderException)
{
    ConsoleUi.Error(e.Message);
    return 1;
}
catch (OperationCanceledException)
{
    return 130;
}

static async Task<int> AskAsync(SettingsStore store, string? providerOverride, string question)
{
    var provider = CliCommon.ResolveProvider(store, providerOverride);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var shell = OperatingSystem.IsWindows()
        ? "PowerShell/cmd"
        : Path.GetFileName(Environment.GetEnvironmentVariable("SHELL") ?? "sh");
    var system = $"""
        You are "como", a command-line assistant. The user asks how to do something in a terminal.
        Environment: {RuntimeInformation.OSDescription}, shell: {shell}.
        Answer format (strict):
        1. A brief summary/explanation of the approach (1-3 short sentences, plain text).
        2. A line containing exactly {ResponseRenderer.Marker}
        3. Only the command(s), one per line, with no markdown fences, no comments and no extra text.
        Never use markdown formatting. Answer in the language of the question.
        """;

    // El nombre del programa ("como") es la primera palabra de la pregunta: se restaura.
    var full = "como " + question;

    var renderer = new ResponseRenderer();
    await foreach (var chunk in provider.AskAsync(full, new AskContext(store, system), cts.Token))
        renderer.Add(chunk);
    renderer.Complete();
    return 0;
}
