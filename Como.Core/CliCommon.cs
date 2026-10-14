using Como.Providers;
using Como.Settings;

namespace Como;

/// <summary>Opciones globales ya extraídas; <c>Command</c> son los argumentos restantes.</summary>
public sealed record CliOptions(string[] Command, bool NoColor, string? Provider);

/// <summary>Parte común de la CLI de todos los ejecutables: opciones globales, -get, -set, -providers, -models.</summary>
public static class CliCommon
{
    /// <summary>Extrae -no-color y -provider &lt;id&gt; de cualquier posición.</summary>
    public static bool TryParse(string[] args, out CliOptions options)
    {
        var rest = new List<string>(args.Length);
        var noColor = false;
        string? provider = null;
        options = new CliOptions([], false, null);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-no-color":
                    noColor = true;
                    break;
                case "-provider":
                    if (i + 1 >= args.Length)
                    {
                        ConsoleUi.Error("Uso: -provider <nombre>");
                        return false;
                    }
                    provider = args[++i];
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        options = new CliOptions(rest.ToArray(), noColor, provider);
        return true;
    }

    /// <summary>Ejecuta -get/-set/-providers/-models. Devuelve null si el comando no es uno de ellos.</summary>
    public static async Task<int?> TryRunAsync(CliOptions o, SettingsStore store)
    {
        if (o.Command.Length == 0) return null;
        return o.Command[0] switch
        {
            "-get" => Get(store, o.Command),
            "-set" => Set(store, o.Command),
            "-providers" => Providers(store, o.Provider),
            "-models" => await ModelsAsync(store, o.Provider),
            _ => null,
        };
    }

    public static IAiProvider ResolveProvider(ISettings store, string? providerOverride)
    {
        var id = providerOverride ?? store.Get(KnownKeys.Provider) ?? ProviderRegistry.DefaultProviderId;
        return ProviderRegistry.Find(id)
            ?? throw new ProviderException(
                $"Proveedor desconocido: {id}. Disponibles: {string.Join(", ", ProviderRegistry.All.Select(x => x.Id))}");
    }

    private static string? EffectiveValue(SettingsStore store, KnownKey k) =>
        k.ProviderId is null
            ? store.Get(k.FullName) ?? k.Key.Default
            : k.Key.Resolve(store, k.ProviderId);

    public static string Mask(string v) => v.Length <= 8 ? new string('*', v.Length) : v[..4] + "…" + v[^4..];

    private static int Providers(SettingsStore store, string? providerOverride)
    {
        var current = ResolveProvider(store, providerOverride).Id;
        foreach (var p in ProviderRegistry.All)
            WriteChoice(p.Id, p.Id == current);
        return 0;
    }

    private static async Task<int> ModelsAsync(SettingsStore store, string? providerOverride)
    {
        var provider = ResolveProvider(store, providerOverride);
        var current = provider.Keys.First(k => k.Name == provider.ModelKeyName).Resolve(store, provider.Id);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        foreach (var m in await provider.ListModelsAsync(store, cts.Token))
            WriteChoice(m, m == current);
        return 0;
    }

    private static void WriteChoice(string name, bool active)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(name);
            return;
        }
        ConsoleUi.Write(active ? "* " : "  ", ConsoleColor.Green);
        ConsoleUi.Write(name, active ? ConsoleColor.White : ConsoleColor.Cyan);
        if (active) ConsoleUi.Write("  (en uso)", ConsoleColor.DarkGray);
        Console.WriteLine();
    }

    private static int Get(SettingsStore store, string[] args)
    {
        if (args.Length == 1)
        {
            var all = KnownKeys.All().ToList();
            var width = all.Max(k => k.FullName.Length);
            foreach (var k in all)
            {
                var stored = store.Get(k.FullName);
                var value = EffectiveValue(store, k);
                var shown = value is null ? "(sin valor)"
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
            ConsoleUi.Error("Uso: -get [clave]");
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

    private static int Set(SettingsStore store, string[] args)
    {
        if (args.Length < 3)
        {
            ConsoleUi.Error("Uso: -set <clave> <valor>");
            return 1;
        }

        var key = KnownKeys.Find(args[1]);
        if (key is null)
        {
            ConsoleUi.Error($"Clave desconocida: {args[1]}. Usa '-get' para ver las disponibles.");
            return 1;
        }

        var value = string.Join(' ', args.Skip(2));

        switch (key.FullName)
        {
            case KnownKeys.Provider:
                var p = ProviderRegistry.Find(value);
                if (p is null)
                {
                    ConsoleUi.Error($"Proveedor desconocido: {value}. Disponibles: {string.Join(", ", ProviderRegistry.All.Select(x => x.Id))}");
                    return 1;
                }
                value = p.Id;
                break;

            case KnownKeys.NoColor:
                if (!value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleUi.Error("no-color admite 'true' o 'false'.");
                    return 1;
                }
                value = value.ToLowerInvariant();
                break;

            default:
                if (key.Key.Validate?.Invoke(value) is { } problem)
                {
                    ConsoleUi.Error($"{key.FullName}: {problem}");
                    return 1;
                }
                break;
        }

        store.Set(key.FullName, value);
        var shown = key.Key.Secret ? Mask(value) : value;
        ConsoleUi.Write("✔ ", ConsoleColor.Green);
        ConsoleUi.Write(key.FullName, ConsoleColor.Cyan);
        ConsoleUi.Write(" guardada con el valor ", ConsoleColor.Green);
        ConsoleUi.WriteLine(shown, ConsoleColor.White);
        return 0;
    }
}
