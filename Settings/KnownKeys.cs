using Como.Providers;

namespace Como.Settings;

/// <summary>Catálogo de todas las claves válidas: globales + las que declara cada proveedor.</summary>
public sealed record KnownKey(string FullName, ConfigKey Key, string? ProviderId);

public static class KnownKeys
{
    public const string Provider = "provider";
    public const string NoColor = "no-color";

    private static readonly ConfigKey ProviderKey =
        new(Provider, "Proveedor de IA en uso", Default: ProviderRegistry.DefaultProviderId);

    private static readonly ConfigKey NoColorKey =
        new(NoColor, "Desactivar colores (true/false)", Default: "false");

    public static IEnumerable<KnownKey> All()
    {
        yield return new KnownKey(Provider, ProviderKey, null);
        yield return new KnownKey(NoColor, NoColorKey, null);
        foreach (var p in ProviderRegistry.All)
            foreach (var k in p.Keys)
                yield return new KnownKey(ConfigKey.FullName(p.Id, k.Name), k, p.Id);
    }

    public static KnownKey? Find(string fullName) =>
        All().FirstOrDefault(k => string.Equals(k.FullName, fullName, StringComparison.OrdinalIgnoreCase));

    public static bool IsTrue(string? v) =>
        v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");
}
