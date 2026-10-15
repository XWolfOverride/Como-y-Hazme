using System.Net;
using System.Text.Json;

namespace Como.Connectors;

/// <summary>Utilidades HTTP compartidas por los conectores (reintentos con backoff exponencial).</summary>
public static class HttpUtil
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Envía la petición con reintentos y backoff exponencial (con jitter) ante errores transitorios:
    /// 429, 500, 502, 503, 504, 529 y fallos de red. Respeta la cabecera Retry-After.
    /// Solo se reintenta antes de recibir la respuesta, así que nunca se duplica salida en streaming.
    /// Recibe una fábrica porque un HttpRequestMessage no puede enviarse dos veces.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> buildRequest, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? resp = null;
            string reason;
            TimeSpan? retryAfter = null;

            try
            {
                using var req = buildRequest();
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode) return resp;

                var code = (int)resp.StatusCode;
                var err = await resp.Content.ReadAsStringAsync(ct);
                var detail = ExtractError(err) ?? err;
                if (!IsTransient(resp.StatusCode) || attempt >= MaxRetries)
                {
                    resp.Dispose();
                    throw new ConnectorException($"Error {code}: {detail}");
                }

                retryAfter = resp.Headers.RetryAfter?.Delta;
                reason = $"Error {code}";
                resp.Dispose();
            }
            catch (HttpRequestException e)
            {
                if (attempt >= MaxRetries)
                    throw new ConnectorException($"No se pudo conectar con el servicio de IA: {e.Message}");
                reason = "Fallo de conexión";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxRetries) throw new ConnectorException("Tiempo de espera agotado.");
                reason = "Tiempo de espera agotado";
            }

            var delay = retryAfter ?? BaseDelay * Math.Pow(2, attempt);
            delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            if (delay > MaxDelay) delay = MaxDelay;

            ConsoleUi.WriteLine(
                $"{reason}; reintentando en {delay.TotalSeconds:0.#}s ({attempt + 1}/{MaxRetries})…",
                ConsoleColor.DarkGray, error: true);
            await Task.Delay(delay, ct);
        }
    }

    private static bool IsTransient(HttpStatusCode code) => (int)code is 429 or 500 or 502 or 503 or 504 or 529;

    /// <summary>Extrae "error.message" de un JSON de error (formato común a Anthropic y Google).</summary>
    public static string? ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return m.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public static int ParsePositive(string? value, int fallback) =>
        int.TryParse(value, out var n) && n > 0 ? n : fallback;
}
