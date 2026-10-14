using Como.Providers;
using Como.Settings;

namespace Hazme.Chat;

// Todo el soporte de herramientas (tool use) vive solo en "hazme": Como.Core no sabe que existe.

/// <summary>Herramienta ofrecida al modelo; <c>ParametersJson</c> es un JSON Schema de tipo object.</summary>
internal sealed record ToolSpec(string Name, string Description, string ParametersJson);

internal sealed record ToolCall(string Id, string Name, string ArgumentsJson);

internal sealed record ToolResult(string CallId, string Name, string Content, bool IsError = false);

internal enum ChatRole { User, Assistant }

/// <summary>
/// Mensaje del historial. Un mensaje de usuario puede llevar resultados de herramientas y/o texto.
/// Un mensaje del asistente conserva el contenido tal cual lo devolvió el proveedor
/// (<c>RawContentJson</c>), que el mismo cliente reenvía sin tocar.
/// </summary>
internal sealed record ChatMessage(
    ChatRole Role,
    string? Text = null,
    IReadOnlyList<ToolResult>? ToolResults = null,
    string? RawContentJson = null);

internal sealed record ChatRequest(string SystemPrompt, IReadOnlyList<ToolSpec> Tools, int MinMaxTokens = 4096);

internal sealed record ChatTurn(string Text, IReadOnlyList<ToolCall> ToolCalls, string RawContentJson);

/// <summary>Un turno de conversación con herramientas; el texto sale en streaming por <c>onText</c>.</summary>
internal interface IChatClient
{
    Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> history, ChatRequest request, Action<string> onText, CancellationToken ct);
}

internal static class ChatClients
{
    /// <summary>Cliente de herramientas para el proveedor; cada proveedor nuevo debe añadirse aquí.</summary>
    public static IChatClient For(IAiProvider provider, ISettings settings) => provider.Id switch
    {
        "claude" => new ClaudeChat(provider, settings),
        "gemini" => new GeminiChat(provider, settings),
        _ => throw new ProviderException($"El proveedor '{provider.Id}' no está soportado por hazme."),
    };
}

internal static class ProviderKeys
{
    public static string Require(IAiProvider p, ISettings s, string name) =>
        p.Keys.First(k => k.Name == name).Require(s, p.Id);

    public static string Resolve(IAiProvider p, ISettings s, string name) =>
        p.Keys.First(k => k.Name == name).Resolve(s, p.Id) ?? "";
}
