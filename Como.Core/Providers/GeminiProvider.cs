using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Como.Settings;

namespace Como.Providers;

public sealed class GeminiProvider : IAiProvider
{
    private static readonly ConfigKey ApiKey = new("api-key", "API key de Google Gemini", Secret: true, EnvVar: "GEMINI_API_KEY");
    private static readonly ConfigKey Model = new("model", "Modelo a usar", Default: "gemini-3.8-flash");
    private static readonly ConfigKey MaxTokens = new("max-tokens", "Máximo de tokens de respuesta", Default: "2048");
    private static readonly ConfigKey BaseUrl = new("base-url", "URL base de la API", Default: "https://generativelanguage.googleapis.com");

    public string Id => "gemini";

    public IReadOnlyList<ConfigKey> Keys { get; } = [ApiKey, Model, MaxTokens, BaseUrl];

    public string ModelKeyName => Model.Name;

    public async Task<IReadOnlyList<string>> ListModelsAsync(ISettings settings, CancellationToken ct)
    {
        var apiKey = ApiKey.Require(settings, Id);
        var baseUrl = BaseUrl.Resolve(settings, Id)!.TrimEnd('/');

        using var resp = await HttpUtil.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1beta/models?pageSize=1000");
            req.Headers.Add("x-goog-api-key", apiKey);
            return req;
        }, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var models))
            foreach (var m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { } name) continue;

                // Solo modelos que sirven para generar contenido.
                if (m.TryGetProperty("supportedGenerationMethods", out var methods)
                    && !methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                    continue;

                ids.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name);
            }
        return ids;
    }

    public async IAsyncEnumerable<string> AskAsync(
        string question, AskContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var apiKey = ApiKey.Require(context.Settings, Id);
        var model = Model.Resolve(context.Settings, Id)!;
        var baseUrl = BaseUrl.Resolve(context.Settings, Id)!.TrimEnd('/');
        var maxTokens = HttpUtil.ParsePositive(MaxTokens.Resolve(context.Settings, Id), 2048);

        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteStartObject("systemInstruction");
            w.WriteStartArray("parts");
            w.WriteStartObject();
            w.WriteString("text", context.SystemPrompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("parts");
            w.WriteStartObject();
            w.WriteString("text", question);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
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
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            using var doc = JsonDocument.Parse(line.Substring(5));
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out _))
                throw new ProviderException(HttpUtil.ExtractError(line.Substring(5)) ?? "Error desconocido del proveedor");

            if (!root.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0) continue;
            if (!cands[0].TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)) continue;

            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } chunk)
                    yield return chunk;
        }
    }
}
