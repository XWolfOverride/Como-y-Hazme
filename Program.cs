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
    // Opciones globales: pueden aparecer en cualquier posición.
    var rest = new List<string>(args.Length);
    var noColorFlag = false;
    string? providerOverride = null;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-no-color":
                noColorFlag = true;
                break;
            case "-provider":
                if (i + 1 >= args.Length)
                {
                    ConsoleUi.Error("Uso: -provider <nombre>");
                    return 1;
                }
                providerOverride = args[++i];
                break;
            default:
                rest.Add(args[i]);
                break;
        }
    }
    var cmd = rest.ToArray();

    if (cmd.Length == 0)
    {
        Console.WriteLine(Usage);
        return 1;
    }

    var store = new SettingsStore();
    ConsoleUi.NoColor = noColorFlag || KnownKeys.IsTrue(store.Get(KnownKeys.NoColor));

    switch (cmd[0])
    {
        case "-h" or "--help" or "-help":
            Console.WriteLine(Usage);
            return 0;
        case "-get":
            return Get(store, cmd);
        case "-set":
            return Set(store, cmd);
        case "-providers":
            return Providers(store, providerOverride);
        case "-models":
            return await ModelsAsync(store, providerOverride);
        default:
            return await AskAsync(store, providerOverride, string.Join(' ', cmd));
    }
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

static string? EffectiveValue(SettingsStore store, KnownKey k) =>
    k.ProviderId is null
        ? store.Get(k.FullName) ?? k.Key.Default
        : k.Key.Resolve(store, k.ProviderId);

static IAiProvider ResolveProvider(SettingsStore store, string? providerOverride)
{
    var id = providerOverride ?? store.Get(KnownKeys.Provider) ?? ProviderRegistry.DefaultProviderId;
    return ProviderRegistry.Find(id)
        ?? throw new ProviderException(
            $"Proveedor desconocido: {id}. Disponibles: {string.Join(", ", ProviderRegistry.All.Select(x => x.Id))}");
}

static int Providers(SettingsStore store, string? providerOverride)
{
    var current = ResolveProvider(store, providerOverride).Id;
    foreach (var p in ProviderRegistry.All)
    {
        var active = p.Id == current;
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(p.Id);
            continue;
        }
        ConsoleUi.Write(active ? "* " : "  ", ConsoleColor.Green);
        ConsoleUi.Write(p.Id, active ? ConsoleColor.White : ConsoleColor.Cyan);
        if (active) ConsoleUi.Write("  (en uso)", ConsoleColor.DarkGray);
        Console.WriteLine();
    }
    return 0;
}

static async Task<int> ModelsAsync(SettingsStore store, string? providerOverride)
{
    var provider = ResolveProvider(store, providerOverride);
    var current = provider.Keys.First(k => k.Name == provider.ModelKeyName).Resolve(store, provider.Id);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    foreach (var m in await provider.ListModelsAsync(store, cts.Token))
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(m);
            continue;
        }
        var active = m == current;
        ConsoleUi.Write(active ? "* " : "  ", ConsoleColor.Green);
        ConsoleUi.Write(m, active ? ConsoleColor.White : ConsoleColor.Cyan);
        if (active) ConsoleUi.Write("  (en uso)", ConsoleColor.DarkGray);
        Console.WriteLine();
    }
    return 0;
}

static int Get(SettingsStore store, string[] args)
{
    if (args.Length == 1)
    {
        var all = KnownKeys.All().ToList();
        var width = all.Max(k => k.FullName.Length);
        foreach (var k in all)
        {
            var stored = store.Get(k.FullName);
            var value = EffectiveValue(store, k);
            string shown = value is null ? "(sin valor)"
                : k.Key.Secret ? Mask(value)
                : value;

            ConsoleUi.Write(k.FullName.PadRight(width), ConsoleColor.Cyan);
            Console.Write("  ");
            ConsoleUi.Write(shown, value is null ? ConsoleColor.DarkGray : ConsoleColor.White);
            if (stored is null && value is not null && k.Key.Default == value)
                ConsoleUi.Write(" (por defecto)", ConsoleColor.DarkGray);
            ConsoleUi.Write("  # " + k.Key.Description, ConsoleColor.DarkGray);
            Console.WriteLine();
        }
        return 0;
    }

    if (args.Length != 2)
    {
        ConsoleUi.Error("Uso: como -get [clave]");
        return 1;
    }

    var key = KnownKeys.Find(args[1]);
    if (key is null)
    {
        ConsoleUi.Error($"Clave desconocida: {args[1]}");
        return 1;
    }

    var v = EffectiveValue(store, key);
    if (v is null) return 1;
    Console.WriteLine(v);
    return 0;
}

static int Set(SettingsStore store, string[] args)
{
    if (args.Length < 3)
    {
        ConsoleUi.Error("Uso: como -set <clave> <valor>");
        return 1;
    }

    var key = KnownKeys.Find(args[1]);
    if (key is null)
    {
        ConsoleUi.Error($"Clave desconocida: {args[1]}. Usa 'como -get' para ver las disponibles.");
        return 1;
    }

    var value = string.Join(' ', args.Skip(2));

    if (key.FullName == KnownKeys.Provider)
    {
        var p = ProviderRegistry.Find(value);
        if (p is null)
        {
            ConsoleUi.Error($"Proveedor desconocido: {value}. Disponibles: {string.Join(", ", ProviderRegistry.All.Select(x => x.Id))}");
            return 1;
        }
        value = p.Id;
    }
    else if (key.FullName == KnownKeys.NoColor)
    {
        if (!value.Equals("true", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleUi.Error("no-color admite 'true' o 'false'.");
            return 1;
        }
        value = value.ToLowerInvariant();
    }

    store.Set(key.FullName, value);
    var shown = key.Key.Secret ? Mask(value) : value;
    ConsoleUi.Write("✔ ", ConsoleColor.Green);
    ConsoleUi.Write(key.FullName, ConsoleColor.Cyan);
    ConsoleUi.Write(" guardada con el valor ", ConsoleColor.Green);
    ConsoleUi.WriteLine(shown, ConsoleColor.White);
    return 0;
}

static async Task<int> AskAsync(SettingsStore store, string? providerOverride, string question)
{
    var provider = ResolveProvider(store, providerOverride);

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

static string Mask(string v) => v.Length <= 8 ? new string('*', v.Length) : v[..4] + "…" + v[^4..];
