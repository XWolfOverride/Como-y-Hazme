using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Como.Settings;

namespace Como.Connectors;

/// <summary>
/// Conector para servicios que hablan el protocolo "Chat Completions" de OpenAI, hoy un estándar de facto
/// que implementan muchos servicios (OpenAI, xAI/Grok, Groq, OpenRouter, Mistral, Ollama, LM Studio...).
/// La <c>base-url</c> incluye la versión (p. ej. https://api.openai.com/v1).
/// </summary>
public abstract class OpenAiCompatibleConnector : IAiConnector
{
    private readonly ConfigKey _apiKey;
    private readonly ConfigKey _model;
    private readonly ConfigKey _baseUrl;

    protected OpenAiCompatibleConnector(
        string id, string vendor, string defaultBaseUrl, string defaultModel, string apiKeyEnvVar)
    {
        Id = id;
        _apiKey = new ConfigKey("api-key", $"API key de {vendor}", Secret: true, EnvVar: apiKeyEnvVar);
        _model = new ConfigKey("model", "Modelo a usar", Default: defaultModel);
        _baseUrl = new ConfigKey("base-url", "URL base de la API (incluye /v1)", Default: defaultBaseUrl);
        Keys = [_apiKey, _model, _baseUrl];
    }

    public string Id { get; }

    public IReadOnlyList<ConfigKey> Keys { get; }

    public string ModelKeyName => _model.Name;

    public async Task<IReadOnlyList<string>> ListModelsAsync(ISettings settings, CancellationToken ct)
    {
        var apiKey = _apiKey.Require(settings, Id);
        var baseUrl = _baseUrl.Resolve(settings, Id)!.TrimEnd('/');

        using var resp = await HttpUtil.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return req;
        }, ct);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var m in data.EnumerateArray())
                if (m.TryGetProperty("id", out var id) && id.GetString() is { } s)
                    ids.Add(s);
        return ids;
    }

    public async IAsyncEnumerable<string> AskAsync(
        string question, AskContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var apiKey = _apiKey.Require(context.Settings, Id);
        var model = _model.Resolve(context.Settings, Id)!;
        var baseUrl = _baseUrl.Resolve(context.Settings, Id)!.TrimEnd('/');

        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", true);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", context.SystemPrompt);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", question);
            w.WriteEndObject();
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

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var json = line.Substring(5).Trim();
            if (json == "[DONE]") yield break;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
                throw new ConnectorException(HttpUtil.ExtractError(json) ?? "Error desconocido del conector");

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } chunk)
                yield return chunk;
        }
    }
}

/// <summary>
/// OpenAI y, cambiando <c>base-url</c>, cualquier servicio compatible con su protocolo
/// (xAI/Grok, Groq, OpenRouter, Mistral, Ollama, LM Studio...).
/// </summary>
public sealed class OpenAiConnector()
    : OpenAiCompatibleConnector("openai", "OpenAI (o del servicio compatible)",
        "https://api.openai.com/v1", "gpt-4.1-mini", "OPENAI_API_KEY");
