using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Como.Connectors;
using Como.Settings;

namespace Hazme.Chat;

/// <summary>
/// Herramientas sobre la API Responses de OpenAI (/v1/responses): la que exigen los modelos de razonamiento con
/// herramientas y los de la familia codex. Sin estado en el servidor: <c>store:false</c> y el razonamiento viaja cifrado
/// (<c>reasoning.encrypted_content</c>) en nuestras propias peticiones.
///
/// El contenido de cada turno del asistente se guarda ya saneado (sin ids ni estados, que el servidor no conserva con
/// store:false) como un array de elementos <c>reasoning</c>, <c>function_call</c> y <c>message</c>, y se reenvía tal cual.
/// </summary>
internal sealed class OpenAiResponsesChat(IAiConnector connector, ISettings settings) : IChatClient
{
    // Algún servicio rechazó el razonamiento cifrado (modelo sin razonamiento): el resto de la sesión va sin él.
    private static bool s_noEncryptedReasoning;

    public async Task<ChatTurn> ChatAsync(
        IReadOnlyList<ChatMessage> history, ChatRequest request, Action<string> onText, CancellationToken ct)
    {
        var apiKey = ConnectorKeys.Require(connector, settings, "api-key");
        var model = ConnectorKeys.Resolve(connector, settings, "model");
        var baseUrl = ConnectorKeys.Resolve(connector, settings, "base-url").TrimEnd('/');
        var effort = ConnectorKeys.Resolve(connector, settings, "reasoning-effort");

        Task<HttpResponseMessage> Send(bool includeReasoning)
        {
            var payload = BuildPayload(model, effort, includeReasoning, history, request);
            return HttpUtil.SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/responses")
                {
                    Content = new ByteArrayContent(payload)
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                return req;
            }, ct);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await Send(!s_noEncryptedReasoning);
        }
        catch (ConnectorException e) when (!s_noEncryptedReasoning
                                           && e.Message.Contains("encrypted_content", StringComparison.OrdinalIgnoreCase))
        {
            s_noEncryptedReasoning = true;
            resp = await Send(false);
        }
        using var respGuard = resp;

        var text = new StringBuilder();
        var items = new SortedDictionary<int, JsonElement>();
        var outputFromCompleted = new List<JsonElement>();
        using var keep = new KeepAlive();
        string? status = null, incompleteReason = null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line.Substring(5).Trim();
            if (json.Length == 0 || json == "[DONE]") continue;

            var doc = JsonDocument.Parse(json);
            keep.Add(doc); // los elementos guardados siguen apuntando a este documento
            var root = doc.RootElement;
            switch (Str(root, "type"))
            {
                case "response.output_text.delta":
                    if (Str(root, "delta") is { Length: > 0 } chunk)
                    {
                        text.Append(chunk);
                        onText(chunk);
                    }
                    break;

                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item))
                        items[root.TryGetProperty("output_index", out var oi) && oi.TryGetInt32(out var idx) ? idx : items.Count] = item;
                    break;

                case "response.completed" or "response.incomplete":
                    if (root.TryGetProperty("response", out var r))
                    {
                        status = Str(r, "status");
                        if (r.TryGetProperty("incomplete_details", out var inc) && inc.ValueKind == JsonValueKind.Object)
                            incompleteReason = Str(inc, "reason");
                        if (r.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                            outputFromCompleted.AddRange(output.EnumerateArray());
                    }
                    break;

                case "response.failed":
                    throw new ConnectorException(
                        root.TryGetProperty("response", out var fr) && fr.TryGetProperty("error", out var fe)
                            ? Str(fe, "message") ?? "La respuesta falló en el servicio de IA"
                            : "La respuesta falló en el servicio de IA");

                case "error":
                    throw new ConnectorException(Str(root, "message") ?? HttpUtil.ExtractError(json) ?? "Error desconocido del servicio de IA");
            }
        }

        // Si el servicio no mandó los elementos sueltos, se usa la salida de la respuesta completa.
        var ordered = items.Count > 0 ? items.Values.ToList() : outputFromCompleted;

        var calls = new List<ToolCall>();
        using var raw = new MemoryStream();
        using (var w = new Utf8JsonWriter(raw))
        {
            w.WriteStartArray();
            foreach (var item in ordered)
                WriteSanitized(w, item, calls);

            if (calls.Count == 0 && text.Length == 0)
            {
                // Respuesta vacía: un mensaje mínimo para que el historial siga siendo válido.
                WriteAssistantMessage(w, "(sin respuesta)");
            }
            w.WriteEndArray();
        }

        string? notice = (status, incompleteReason) switch
        {
            (_, "max_output_tokens") => "Your last response hit the output limit (reasoning tokens count too) and was cut off. Anything after the cut was lost: write less per response or in several steps.",
            (_, "content_filter") => "The service filtered your last response (incomplete: content_filter).",
            ("failed" or "cancelled", _) => $"The service ended your last response abnormally (status: {status}).",
            _ when text.Length == 0 && calls.Count == 0 => $"The service returned an empty response (status: {status ?? "none"}).",
            _ => null,
        };
        return new ChatTurn(text.ToString(), calls, Encoding.UTF8.GetString(raw.ToArray()), notice);
    }

    private static byte[] BuildPayload(
        string model, string effort, bool includeReasoning, IReadOnlyList<ChatMessage> history, ChatRequest request)
    {
        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("instructions", request.SystemPrompt);
            w.WriteBoolean("stream", true);
            w.WriteBoolean("store", false);
            if (effort.Length > 0)
            {
                w.WriteStartObject("reasoning");
                w.WriteString("effort", effort);
                w.WriteEndObject();
            }
            if (includeReasoning)
            {
                w.WriteStartArray("include");
                w.WriteStringValue("reasoning.encrypted_content");
                w.WriteEndArray();
            }

            if (request.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var t in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("parameters");
                    w.WriteRawValue(t.ParametersJson);
                    // En Responses las funciones son estrictas por defecto (todos los campos obligatorios): las nuestras tienen opcionales.
                    w.WriteBoolean("strict", false);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteStartArray("input");
            foreach (var m in history)
            {
                if (m.Role == ChatRole.Assistant && m.RawContentJson is not null)
                {
                    using var items = JsonDocument.Parse(m.RawContentJson);
                    foreach (var item in items.RootElement.EnumerateArray()) item.WriteTo(w);
                    continue;
                }

                foreach (var r in m.ToolResults ?? [])
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function_call_output");
                    w.WriteString("call_id", r.CallId);
                    w.WriteString("output", r.Content);
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
        return body.ToArray();
    }

    /// <summary>Reduce un elemento de salida a lo que se puede reenviar sin estado: sin id ni status.</summary>
    private static void WriteSanitized(Utf8JsonWriter w, JsonElement item, List<ToolCall> calls)
    {
        switch (Str(item, "type"))
        {
            case "reasoning":
                // Sin contenido cifrado no se puede reenviar (el servidor no lo guarda): se descarta.
                if (Str(item, "encrypted_content") is not { Length: > 0 } enc) return;
                w.WriteStartObject();
                w.WriteString("type", "reasoning");
                w.WritePropertyName("summary");
                if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
                {
                    summary.WriteTo(w);
                }
                else
                {
                    w.WriteStartArray();
                    w.WriteEndArray();
                }
                w.WriteString("encrypted_content", enc);
                w.WriteEndObject();
                break;

            case "function_call":
            {
                var call = ToolCall.Create(Str(item, "call_id") ?? Str(item, "id") ?? "", Str(item, "name") ?? "",
                    Str(item, "arguments") ?? "");
                calls.Add(call);
                w.WriteStartObject();
                w.WriteString("type", "function_call");
                w.WriteString("call_id", call.Id);
                w.WriteString("name", call.Name);
                w.WriteString("arguments", call.ArgumentsJson);
                w.WriteEndObject();
                break;
            }

            case "message":
            {
                var sb = new StringBuilder();
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                        if (Str(part, "type") == "output_text" && Str(part, "text") is { } t)
                            sb.Append(t);
                if (sb.Length > 0) WriteAssistantMessage(w, sb.ToString());
                break;
            }
        }
    }

    private static void WriteAssistantMessage(Utf8JsonWriter w, string text)
    {
        w.WriteStartObject();
        w.WriteString("type", "message");
        w.WriteString("role", "assistant");
        w.WriteStartArray("content");
        w.WriteStartObject();
        w.WriteString("type", "output_text");
        w.WriteString("text", text);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Mantiene vivos los documentos JSON de los que cuelgan los elementos guardados hasta terminar la respuesta.</summary>
    private sealed class KeepAlive : IDisposable
    {
        private readonly List<JsonDocument> _docs = [];
        public void Add(JsonDocument d) => _docs.Add(d);
        public void Dispose() { foreach (var d in _docs) d.Dispose(); }
    }
}
