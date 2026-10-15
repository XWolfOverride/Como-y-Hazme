using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Como.Connectors;
using Como.Settings;

namespace Hazme.Chat;

internal sealed class GeminiChat(IAiConnector connector, ISettings settings) : IChatClient
{
    // Los ids sintéticos llevan este prefijo: Gemini no siempre devuelve id en functionCall.
    private const string SyntheticIdPrefix = "g_";

    public async Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> history, ChatRequest request, Action<string> onText, CancellationToken ct)
    {
        var apiKey = ConnectorKeys.Require(connector, settings, "api-key");
        var model = ConnectorKeys.Resolve(connector, settings, "model");
        var baseUrl = ConnectorKeys.Resolve(connector, settings, "base-url").TrimEnd('/');
        var maxTokens = Math.Max(
            HttpUtil.ParsePositive(ConnectorKeys.Resolve(connector, settings, "max-tokens"), 2048),
            request.MinMaxTokens);

        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();

            w.WriteStartObject("systemInstruction");
            w.WriteStartArray("parts");
            w.WriteStartObject();
            w.WriteString("text", request.SystemPrompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();

            if (request.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                w.WriteStartObject();
                w.WriteStartArray("functionDeclarations");
                foreach (var t in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("parameters");
                    w.WriteRawValue(t.ParametersJson);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteStartArray("contents");
            foreach (var m in history)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role == ChatRole.User ? "user" : "model");
                w.WritePropertyName("parts");
                if (m.Role == ChatRole.Assistant && m.RawContentJson is not null)
                {
                    w.WriteRawValue(m.RawContentJson);
                }
                else
                {
                    w.WriteStartArray();
                    foreach (var r in m.ToolResults ?? [])
                    {
                        w.WriteStartObject();
                        w.WriteStartObject("functionResponse");
                        w.WriteString("name", r.Name);
                        if (!r.CallId.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal))
                            w.WriteString("id", r.CallId);
                        w.WriteStartObject("response");
                        w.WriteString(r.IsError ? "error" : "output", r.Content);
                        w.WriteEndObject();
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    if (!string.IsNullOrEmpty(m.Text) || (m.ToolResults is null or { Count: 0 }))
                    {
                        w.WriteStartObject();
                        w.WriteString("text", string.IsNullOrEmpty(m.Text) ? "(vacío)" : m.Text);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("generationConfig");
            w.WriteNumber("maxOutputTokens", maxTokens);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        var url = $"{baseUrl}/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";
        var payload = body.ToArray();
        using var resp = await HttpUtil.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(payload)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Add("x-goog-api-key", apiKey);
            return req;
        }, ct);

        var fullText = new StringBuilder();
        var pendingText = new StringBuilder();
        var rawParts = new List<string>();
        var calls = new List<ToolCall>();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            using var doc = JsonDocument.Parse(line.Substring(5));
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
                throw new ConnectorException(HttpUtil.ExtractError(line.Substring(5)) ?? "Error desconocido del servicio de IA");

            if (!root.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0) continue;
            if (!cands[0].TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)) continue;

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var fc))
                {
                    if (pendingText.Length > 0)
                    {
                        rawParts.Add(TextPart(pendingText.ToString()));
                        pendingText.Clear();
                    }
                    var name = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var id = fc.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } real
                        ? real
                        : SyntheticIdPrefix + calls.Count;
                    var args = fc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                        ? a.GetRawText()
                        : "{}";
                    calls.Add(new ToolCall(id, name, args));
                    rawParts.Add(part.GetRawText()); // conserva thoughtSignature si viene
                }
                else if (part.TryGetProperty("text", out var text)
                         && !(part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True)
                         && text.GetString() is { Length: > 0 } chunk)
                {
                    pendingText.Append(chunk);
                    fullText.Append(chunk);
                    onText(chunk);
                }
            }
        }

        if (pendingText.Length > 0) rawParts.Add(TextPart(pendingText.ToString()));
        if (rawParts.Count == 0) rawParts.Add(TextPart("(sin respuesta)"));

        return new ChatTurn(fullText.ToString(), calls, "[" + string.Join(",", rawParts) + "]");
    }

    private static string TextPart(string text)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("text", text);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
