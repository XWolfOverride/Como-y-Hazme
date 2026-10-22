using Como.Connectors;
using Como.Settings;

namespace Hazme.Chat;

// Todo el soporte de herramientas (tool use) vive solo en "hazme": Como.Core no sabe que existe.

/// <summary>Herramienta ofrecida al modelo; <c>ParametersJson</c> es un JSON Schema de tipo object.</summary>
internal sealed record ToolSpec(string Name, string Description, string ParametersJson);

/// <summary>Llamada a una herramienta. <c>Error</c> indica que los argumentos llegaron inservibles (p. ej. salida cortada).</summary>
internal sealed record ToolCall(string Id, string Name, string ArgumentsJson, string? Error = null)
{
    private const string BadArguments =
        "The arguments of this call were not valid JSON, most likely because your output hit the length limit and was cut off in the middle of the call, so NOTHING was received. " +
        "Do not repeat the same call. Send much less content per call: split the file into several parts and write them one by one " +
        "(write_file with append=true, or run_command with cat >> file and a heredoc).";

    /// <summary>Crea la llamada validando los argumentos: JSON vacío o inválido se convierte en {} con un error explícito.</summary>
    public static ToolCall Create(string id, string name, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new ToolCall(id, name, "{}");
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (d.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object) return new ToolCall(id, name, argumentsJson);
        }
        catch (System.Text.Json.JsonException) { }
        return new ToolCall(id, name, "{}", BadArguments);
    }
}

internal sealed record ToolResult(string CallId, string Name, string Content, bool IsError = false);

internal enum ChatRole { User, Assistant }

/// <summary>
/// Mensaje del historial. Un mensaje de usuario puede llevar resultados de herramientas y/o texto.
/// Un mensaje del asistente conserva el contenido tal cual lo devolvió el conector
/// (<c>RawContentJson</c>), que el mismo cliente reenvía sin tocar.
/// </summary>
internal sealed record ChatMessage(
    ChatRole Role,
    string? Text = null,
    IReadOnlyList<ToolResult>? ToolResults = null,
    string? RawContentJson = null);

internal sealed record ChatRequest(string SystemPrompt, IReadOnlyList<ToolSpec> Tools, int MinMaxTokens = 16384);

/// <summary>Turno de la IA. <c>Notice</c>: problema del lado del servicio (salida cortada, bloqueo...) para informar a la IA.</summary>
internal sealed record ChatTurn(string Text, IReadOnlyList<ToolCall> ToolCalls, string RawContentJson, string? Notice = null);

/// <summary>Un turno de conversación con herramientas; el texto sale en streaming por <c>onText</c>.</summary>
internal interface IChatClient
{
    Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> history, ChatRequest request, Action<string> onText, CancellationToken ct);
}

internal static class ChatClients
{
    /// <summary>Cliente de herramientas para el conector; cada conector nuevo debe añadirse aquí.</summary>
    public static IChatClient For(IAiConnector connector, ISettings settings) => connector.Id switch
    {
        "claude" => new ClaudeChat(connector, settings),
        "gemini" => new GeminiChat(connector, settings),
        "openai" => ConnectorKeys.Resolve(connector, settings, "api") switch
        {
            "responses" => new OpenAiResponsesChat(connector, settings),
            "auto" => new OpenAiAutoChat(connector, settings),
            _ => new OpenAiChat(connector, settings),
        },
        _ => throw new ConnectorException($"El conector '{connector.Id}' no está soportado por hazme."),
    };
}

internal static class ConnectorKeys
{
    public static string Require(IAiConnector p, ISettings s, string name) =>
        p.Keys.First(k => k.Name == name).Require(s, p.Id);

    public static string Resolve(IAiConnector p, ISettings s, string name) =>
        p.Keys.First(k => k.Name == name).Resolve(s, p.Id) ?? "";
}
