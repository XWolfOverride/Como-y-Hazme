namespace Como.Connectors;

/// <summary>Lista de conectores conocidos (sin reflexión, compatible con NativeAOT).</summary>
public static class ConnectorRegistry
{
    public const string DefaultConnectorId = "claude";

    public static IReadOnlyList<IAiConnector> All { get; } = 
    [
        new ClaudeConnector(),
        new GeminiConnector(),
        new OpenAiConnector(),
    ];

    public static IAiConnector? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
