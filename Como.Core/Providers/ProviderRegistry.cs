namespace Como.Providers;

/// <summary>Lista de proveedores conocidos (sin reflexión, compatible con NativeAOT).</summary>
public static class ProviderRegistry
{
    public const string DefaultProviderId = "claude";

    public static IReadOnlyList<IAiProvider> All { get; } = [new ClaudeProvider(), new GeminiProvider()];

    public static IAiProvider? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
