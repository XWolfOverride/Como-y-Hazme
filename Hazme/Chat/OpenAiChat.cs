using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Como.Connectors;
using Como.Settings;

namespace Hazme.Chat;

/// <summary>
/// Herramientas sobre el protocolo Chat Completions de OpenAI. Sirve para OpenAI y para cualquier
/// servicio compatible que se alcance cambiando la base-url.
/// </summary>
internal sealed class OpenAiChat(IAiConnector connector, ISettings settings) : IChatClient
{
    private sealed class PendingCall
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Args = new();
    }

    public async Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> history, ChatRequest request, Action<string> onText, CancellationToken ct)
    {
        var apiKey = ConnectorKeys.Require(connector, settings, "api-key");
        var model = ConnectorKeys.Resolve(connector, settings, "model");
        var baseUrl = ConnectorKeys.Resolve(connector, settings, "base-url").TrimEnd('/');

        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", true);

            if (request.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var t in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("parameters");
                    w.WriteRawValue(t.ParametersJson);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteStartArray("messages");

            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", request.SystemPrompt);
            w.WriteEndObject();

            foreach (var m in history)
            {
                if (m.Role == ChatRole.Assistant && m.RawContentJson is not null)
                {
                    // El mensaje completo del asistente, tal como lo devolvió el servicio.
                    w.WriteRawValue(m.RawContentJson);
                    continue;
                }

                // Cada resultado de herramienta es su propio mensaje, y deben ir pegados al del asistente.
                foreach (var r in m.ToolResults ?? [])
                {
                    w.WriteStartObject();
                    w.WriteString("role", "tool");
                    w.WriteString("tool_call_id", r.CallId);
                    w.WriteString("content", r.Content);
                    w.WriteEndObject();
                }

                if (!string.IsNullOrEmpty(m.Text) || (m.ToolResults is null or { Count: 0 }))
                {
                    w.WriteStartObject();
                    w.WriteString("role", "user");
                    w.WriteString("content", string.IsNullOrEmpty(m.Text) ? "(vacío)" : m.Text);
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        var payload = body.ToArray();
        using var resp = await HttpUtil.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
            {
                Content = new ByteArrayContent(payload)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return req;
        }, ct);

        var text = new StringBuilder();
        var pending = new SortedDictionary<int, PendingCall>();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var json = line.Substring(5).Trim();
            if (json == "[DONE]") break;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
                throw new ConnectorException(HttpUtil.ExtractError(json) ?? "Error desconocido del conector");

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta)) continue;

            if (delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } chunk)
            {
                text.Append(chunk);
                onText(chunk);
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var index = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n) ? n : pending.Count;
                    if (!pending.TryGetValue(index, out var call))
                        pending[index] = call = new PendingCall();

                    if (tc.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idStr)
                        call.Id = idStr;
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } name)
                            call.Name = name;
                        if (fn.TryGetProperty("arguments", out var ar) && ar.GetString() is { } args)
                            call.Args.Append(args);
                    }
                }
            }
        }

        var calls = new List<ToolCall>();
        foreach (var (index, p) in pending)
        {
            var id = p.Id.Length > 0 ? p.Id : $"call_{index}";
            calls.Add(new ToolCall(id, p.Name, ValidObjectOrEmpty(p.Args.ToString())));
        }

        // Mensaje completo del asistente para reenviarlo en el historial.
        using var raw = new MemoryStream();
        using (var w = new Utf8JsonWriter(raw))
        {
            w.WriteStartObject();
            w.WriteString("role", "assistant");
            if (text.Length > 0) w.WriteString("content", text.ToString());
            else w.WriteNull("content");
            if (calls.Count > 0)
            {
                w.WriteStartArray("tool_calls");
                foreach (var c in calls)
                {
                    w.WriteStartObject();
                    w.WriteString("id", c.Id);
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", c.Name);
                    w.WriteString("arguments", c.ArgumentsJson);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        return new ChatTurn(text.ToString(), calls, Encoding.UTF8.GetString(raw.ToArray()));
    }

    private static string ValidObjectOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.ValueKind == JsonValueKind.Object ? json : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }
}
