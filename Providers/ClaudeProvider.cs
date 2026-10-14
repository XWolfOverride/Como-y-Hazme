using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Como.Providers;

public sealed class ClaudeProvider : IAiProvider
{
    private static readonly ConfigKey ApiKey = new("api-key", "API key de Anthropic", Secret: true, EnvVar: "ANTHROPIC_API_KEY");
    private static readonly ConfigKey Model = new("model", "Modelo a usar", Default: "claude-sonnet-5-5");
    private static readonly ConfigKey MaxTokens = new("max-tokens", "Máximo de tokens de respuesta", Default: "1024");
    private static readonly ConfigKey BaseUrl = new("base-url", "URL base de la API", Default: "https://api.anthropic.com");

    public string Id => "claude";

    public IReadOnlyList<ConfigKey> Keys { get; } = [ApiKey, Model, MaxTokens, BaseUrl];

    public string ModelKeyName => Model.Name;

    public async Task<IReadOnlyList<string>> ListModelsAsync(Como.Settings.ISettings settings, CancellationToken ct)
    {
        var apiKey = ApiKey.Require(settings, Id);
        var baseUrl = BaseUrl.Resolve(settings, Id)!.TrimEnd('/');

        using var resp = await HttpUtil.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models?limit=1000");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
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
        var apiKey = ApiKey.Require(context.Settings, Id);
        var model = Model.Resolve(context.Settings, Id)!;
        var baseUrl = BaseUrl.Resolve(context.Settings, Id)!.TrimEnd('/');
        var maxTokens = HttpUtil.ParsePositive(MaxTokens.Resolve(context.Settings, Id), 1024);

        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);
            w.WriteBoolean("stream", true);
            w.WriteString("system", context.SystemPrompt);
            w.WriteStartArray("messages");
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
            var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/messages")
            {
                Content = new ByteArrayContent(payload)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            return req;
        }, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            using var doc = JsonDocument.Parse(line.Substring(5));
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) continue;

            switch (typeEl.GetString())
            {
                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("text", out var text)
                        && text.GetString() is { Length: > 0 } chunk)
                        yield return chunk;
                    break;
                case "error":
                    throw new ProviderException(HttpUtil.ExtractError(line.Substring(5)) ?? "Error desconocido del proveedor");
                case "message_stop":
                    yield break;
            }
        }
    }
}
