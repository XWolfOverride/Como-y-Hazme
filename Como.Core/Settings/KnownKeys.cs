using Como.Providers;

namespace Como.Settings;

/// <summary>Catálogo de todas las claves válidas: globales + las que declara cada proveedor + extras registradas.</summary>
public sealed record KnownKey(string FullName, ConfigKey Key, string? ProviderId);

public static class KnownKeys
{
    public const string Provider = "provider";
    public const string NoColor = "no-color";

    private static readonly List<ConfigKey> Globals =
    [
        new(Provider, "Proveedor de IA en uso", Default: ProviderRegistry.DefaultProviderId),
        new(NoColor, "Desactivar colores (true/false)", Default: "false"),
    ];

    /// <summary>Permite a un ejecutable añadir sus propias claves globales (p. ej. las de "hazme").</summary>
    public static void Register(params ConfigKey[] keys)
    {
        foreach (var k in keys)
            if (!Globals.Any(g => g.Name == k.Name)) Globals.Add(k);
    }

    public static IEnumerable<KnownKey> All()
    {
        foreach (var k in Globals)
            yield return new KnownKey(k.Name, k, null);
        foreach (var p in ProviderRegistry.All)
            foreach (var k in p.Keys)
                yield return new KnownKey(ConfigKey.FullName(p.Id, k.Name), k, p.Id);
    }

    public static KnownKey? Find(string fullName) =>
        All().FirstOrDefault(k => string.Equals(k.FullName, fullName, StringComparison.OrdinalIgnoreCase));

    public static bool IsTrue(string? v) =>
        v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

    /// <summary>Valor de una clave global: guardado o, si no hay, el valor por defecto.</summary>
    public static string? Value(ISettings settings, string name) =>
        settings.Get(name) ?? Globals.FirstOrDefault(k => k.Name == name)?.Default;

    public static int IntValue(ISettings settings, string name, int fallback) =>
        int.TryParse(Value(settings, name), out var n) && n >= 0 ? n : fallback;
}
