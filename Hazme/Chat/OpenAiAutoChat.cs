using Como;
using Como.Connectors;
using Como.Settings;

namespace Hazme.Chat;

/// <summary>
/// <c>openai.api = auto</c>: Chat Completions, y si el servicio responde que ese modelo solo funciona con
/// /v1/responses, cambia a la API Responses el resto de la sesión. Solo cambia antes de la primera respuesta
/// del asistente, porque el historial de cada API tiene un formato distinto.
/// </summary>
internal sealed class OpenAiAutoChat(IAiConnector connector, ISettings settings) : IChatClient
{
    private static bool s_responses;

    private readonly OpenAiChat _chat = new(connector, settings, explicitChat: false);
    private readonly OpenAiResponsesChat _responses = new(connector, settings);

    public async Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> history, ChatRequest request, Action<string> onText, CancellationToken ct)
    {
        if (s_responses) return await _responses.ChatAsync(history, request, onText, ct);

        try
        {
            return await _chat.ChatAsync(history, request, onText, ct);
        }
        catch (ConnectorException e) when (e.Message.Contains("v1/responses", StringComparison.OrdinalIgnoreCase)
                                           && !history.Any(m => m.Role == ChatRole.Assistant))
        {
            s_responses = true;
            ConsoleUi.WriteLine(
                "↺ El servicio pide la API Responses para este modelo: se usa /v1/responses (openai.api = auto).",
                ConsoleColor.Yellow, error: true);
            return await _responses.ChatAsync(history, request, onText, ct);
        }
    }
}
