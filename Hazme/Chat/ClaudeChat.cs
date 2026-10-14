using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Como.Connectors;
using Como.Settings;

namespace Hazme.Chat;

internal sealed class ClaudeChat(IAiConnector connector, ISettings settings) : IChatClient
{
    private sealed class Block
    {
        public string Type = "";
        public string? Id;
        public string? Name;
        public readonly StringBuilder Data = new();
    }

    public async Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> history, ChatRequest request, Action<string> onText, CancellationToken ct)
    {
        var apiKey = ConnectorKeys.Require(connector, settings, "api-key");
        var model = ConnectorKeys.Resolve(connector, settings, "model");
        var baseUrl = ConnectorKeys.Resolve(connector, settings, "base-url").TrimEnd('/');
        var maxTokens = Math.Max(
            HttpUtil.ParsePositive(ConnectorKeys.Resolve(connector, settings, "max-tokens"), 1024),
            request.MinMaxTokens);

        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);
            w.WriteBoolean("stream", true);
            w.WriteString("system", request.SystemPrompt);

            if (request.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var t in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("input_schema");
                    w.WriteRawValue(t.ParametersJson);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteStartArray("messages");
            foreach (var m in history)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role == ChatRole.User ? "user" : "assistant");
                w.WritePropertyName("content");
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
                        w.WriteString("type", "tool_result");
                        w.WriteString("tool_use_id", r.CallId);
                        w.WriteString("content", r.Content);
                        if (r.IsError) w.WriteBoolean("is_error", true);
                        w.WriteEndObject();
                    }
                    if (!string.IsNullOrEmpty(m.Text) || (m.ToolResults is null or { Count: 0 }))
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "text");
                        w.WriteString("text", string.IsNullOrEmpty(m.Text) ? "(vacío)" : m.Text);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        var payload = body.ToArray();
        using var resp = await HttpUtil.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/messages")
            {
                Content = new ByteArrayContent(payload)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            return req;
        }, ct);

        var blocks = new SortedDictionary<int, Block>();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var done = false;
        while (!done && await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            using var doc = JsonDocument.Parse(line.Substring(5));
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) continue;

            switch (typeEl.GetString())
            {
                case "content_block_start":
                {
                    var cb = root.GetProperty("content_block");
                    blocks[root.GetProperty("index").GetInt32()] = new Block
                    {
                        Type = cb.GetProperty("type").GetString() ?? "",
                        Id = cb.TryGetProperty("id", out var id) ? id.GetString() : null,
                        Name = cb.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                    };
                    break;
                }
                case "content_block_delta":
                {
                    if (!blocks.TryGetValue(root.GetProperty("index").GetInt32(), out var b)) break;
                    var delta = root.GetProperty("delta");
                    if (delta.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } t)
                    {
                        b.Data.Append(t);
                        onText(t);
                    }
                    else if (delta.TryGetProperty("partial_json", out var pj) && pj.GetString() is { } j)
                    {
                        b.Data.Append(j);
                    }
                    break;
                }
                case "error":
                    throw new ConnectorException(HttpUtil.ExtractError(line.Substring(5)) ?? "Error desconocido del conector");
                case "message_stop":
                    done = true;
                    break;
            }
        }

        // Reconstruye el contenido del asistente y extrae texto y llamadas.
        var fullText = new StringBuilder();
        var calls = new List<ToolCall>();
        using var raw = new MemoryStream();
        using (var w = new Utf8JsonWriter(raw))
        {
            w.WriteStartArray();
            foreach (var b in blocks.Values)
            {
                if (b.Type == "text")
                {
                    if (b.Data.Length == 0) continue;
                    fullText.Append(b.Data);
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", b.Data.ToString());
                    w.WriteEndObject();
                }
                else if (b.Type == "tool_use")
                {
                    var args = ValidObjectOrEmpty(b.Data.ToString());
                    calls.Add(new ToolCall(b.Id ?? "", b.Name ?? "", args));
                    w.WriteStartObject();
                    w.WriteString("type", "tool_use");
                    w.WriteString("id", b.Id);
                    w.WriteString("name", b.Name);
                    w.WritePropertyName("input");
                    w.WriteRawValue(args);
                    w.WriteEndObject();
                }
            }
            if (fullText.Length == 0 && calls.Count == 0)
            {
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", "(sin respuesta)");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        return new ChatTurn(fullText.ToString(), calls, Encoding.UTF8.GetString(raw.ToArray()));
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
