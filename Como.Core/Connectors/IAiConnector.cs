using Como.Settings;

namespace Como.Connectors;

/// <summary>Clave de configuración declarada por un conector (se guarda como "&lt;id&gt;.&lt;Name&gt;").</summary>
public sealed record ConfigKey(
    string Name,
    string Description,
    string? Default = null,
    bool Secret = false,
    string? EnvVar = null,
    Func<string, string?>? Validate = null,
    Func<string, string?>? Warn = null)
{
    public static string FullName(string connectorId, string name) => $"{connectorId}.{name}";

    /// <summary>Valor guardado, o variable de entorno, o valor por defecto.</summary>
    public string? Resolve(ISettings settings, string connectorId)
    {
        var v = settings.Get(FullName(connectorId, Name));
        if (!string.IsNullOrEmpty(v)) return v;
        if (EnvVar is not null)
        {
            v = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return Default;
    }

    /// <summary>Como <see cref="Resolve"/>, pero falla con un mensaje claro si no hay valor.</summary>
    public string Require(ISettings settings, string connectorId) =>
        Resolve(settings, connectorId)
        ?? throw new ConnectorException(
            $"Falta la clave '{FullName(connectorId, Name)}'. Configúrala con: -set {FullName(connectorId, Name)} <valor>");
}

public sealed record AskContext(ISettings Settings, string SystemPrompt);

public sealed class ConnectorException(string message) : Exception(message);

/// <summary>
/// Conector de IA. Solo sabe preguntar y listar modelos: no hay aquí ninguna noción de herramientas
/// ni de ejecución; eso vive exclusivamente en el ejecutable "hazme".
/// </summary>
public interface IAiConnector
{
    /// <summary>Identificador usado en la clave global "connector" y como prefijo de sus claves.</summary>
    string Id { get; }

    IReadOnlyList<ConfigKey> Keys { get; }

    /// <summary>Envía la pregunta y devuelve la respuesta en fragmentos (streaming).</summary>
    IAsyncEnumerable<string> AskAsync(string question, AskContext context, CancellationToken ct);

    /// <summary>Identificadores de los modelos disponibles en el conector.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(ISettings settings, CancellationToken ct);

    /// <summary>Nombre de la clave (sin prefijo) que guarda el modelo en uso.</summary>
    string ModelKeyName { get; }
}
