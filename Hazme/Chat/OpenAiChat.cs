using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Como;
using Como.Connectors;
using Como.Settings;

namespace Hazme.Chat;

/// <summary>
/// Herramientas sobre el protocolo Chat Completions de OpenAI. Sirve para OpenAI y para cualquier
/// servicio compatible que se alcance cambiando la base-url.
/// </summary>
internal sealed class OpenAiChat(IAiConnector connector, ISettings settings, bool explicitChat = true) : IChatClient
{
    // Tras un rechazo por razonamiento+herramientas, el resto de la sesión ya va con "none".
    private static string? s_effortFallback;

    private sealed class PendingCall
    {
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Args = new();
    }

    public async Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> history, ChatRequest request, Action<string> onText, CancellationToken ct)
    {
        try
        {
            return await ChatCoreAsync(history, request, onText, ct);
        }
        catch (ConnectorException e) when (explicitChat && e.Message.Contains("v1/responses", StringComparison.OrdinalIgnoreCase)
                                           && !e.Message.Contains("openai.api", StringComparison.Ordinal))
        {
            // El propio servicio dice que ese modelo solo funciona con /v1/responses: se le explica al usuario cómo activarla.
            throw new ConnectorException(e.Message +
                "\nEste modelo solo funciona con la API Responses. Actívala con `hazme -set openai.api responses` " +
                "(o `auto`: usa Chat Completions y cambia sola si el servicio lo pide).");
        }
    }

    private async Task<ChatTurn> ChatCoreAsync(
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

        var basePayload = body.ToArray();

        Task<HttpResponseMessage> Send(string? effort)
        {
            var payload = effort is { Length: > 0 } ? WithReasoningEffort(basePayload, effort) : basePayload;
            return HttpUtil.SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
                {
                    Content = new ByteArrayContent(payload)
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                return req;
            }, ct);
        }

        // Clave "reasoning-effort" (o, si el servicio ya rechazó el razonamiento con herramientas, "none").
        var effort = s_effortFallback ?? ConnectorKeys.Resolve(connector, settings, "reasoning-effort");
        HttpResponseMessage resp;
        try
        {
            resp = await Send(effort);
        }
        catch (ConnectorException e) when (explicitChat && effort != "none"
                                           && e.Message.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
                                           && e.Message.Contains("none", StringComparison.OrdinalIgnoreCase))
        {
            // Algunos modelos de razonamiento no admiten herramientas con razonamiento en Chat Completions.
            ConsoleUi.WriteLine(
                "⚠ El modelo no admite razonamiento con herramientas en Chat Completions: se usa reasoning_effort=none " +
                "(fíjalo con `-set openai.reasoning-effort none` para no ver este aviso).", ConsoleColor.Yellow, error: true);
            s_effortFallback = "none";
            try
            {
                resp = await Send("none");
            }
            catch (ConnectorException e2) when (e2.Message.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
            {
                // Razonamiento obligatorio + herramientas no admitidas en Chat Completions: sin salida con esta API.
                throw new ConnectorException(
                    $"El modelo «{model}» exige razonamiento y no admite herramientas en Chat Completions, que es la API que " +
                    "usa hazme en este modo: actívala con `hazme -set openai.api responses` (o `auto`). " +
                    "Detalle del servicio: " + e2.Message);
            }
        }
        using var respGuard = resp;

        var text = new StringBuilder();
        var pending = new SortedDictionary<int, PendingCall>();
        string? finishReason = null;

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
                throw new ConnectorException(HttpUtil.ExtractError(json) ?? "Error desconocido del servicio de IA");

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finishReason = fr.GetString();
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
            calls.Add(ToolCall.Create(id, p.Name, p.Args.ToString()));
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

        string? notice = finishReason switch
        {
            "length" => "Your last response hit the output limit of the service and was cut off. Anything after the cut was lost: write less per response or in several steps.",
            "content_filter" => "The service filtered your last response (finish_reason: content_filter).",
            _ when text.Length == 0 && calls.Count == 0 => $"The service returned an empty response (finish_reason: {finishReason ?? "none"}).",
            _ => null,
        };
        return new ChatTurn(text.ToString(), calls, Encoding.UTF8.GetString(raw.ToArray()), notice);
    }

    /// <summary>Inserta "reasoning_effort" como primer campo del objeto JSON ya serializado.</summary>
    private static byte[] WithReasoningEffort(byte[] json, string effort)
    {
        var field = Encoding.UTF8.GetBytes($"{{\"reasoning_effort\":\"{JsonEncodedText.Encode(effort)}\",");
        return [.. field, .. json.AsSpan(1)];
    }
}
