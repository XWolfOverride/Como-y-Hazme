using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Como.Settings;

public interface ISettings
{
    string? Get(string key);
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class SettingsJsonContext : JsonSerializerContext;

public sealed class SettingsException(string message) : Exception(message);

/// <summary>
/// Almacén clave/valor cifrado en un único archivo dentro de la carpeta del usuario.
///
/// Formato: "COM1" | salt(16) | nonce(12) | tag(16) | ciphertext (AES-256-GCM, JSON UTF-8).
/// La clave se deriva con SHA-256 de (salt aleatorio del archivo + usuario + carpeta personal),
/// por lo que no hay ningún secreto embebido en el binario y el coste es de microsegundos.
///
/// El archivo es compartido por varias herramientas: se carga y se guarda el diccionario completo,
/// de modo que las claves que esta herramienta no conoce se conservan intactas.
/// </summary>
public sealed class SettingsStore : ISettings
{
    private static readonly byte[] Magic = "COM1"u8.ToArray();

    private readonly string _path;
    private Dictionary<string, string>? _values;

    public SettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public string FilePath => _path;

    public static string DefaultPath()
    {
        var dir = Environment.GetEnvironmentVariable("COMO_CONFIG_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".como");
        return Path.Combine(dir, "config");
    }

    public string? Get(string key)
    {
        Load();
        return _values!.TryGetValue(key, out var v) ? v : null;
    }

    public void Set(string key, string value)
    {
        Load();
        _values![key] = value;
        Save();
    }

    public void Remove(string key)
    {
        Load();
        if (_values!.Remove(key)) Save();
    }

    private void Load()
    {
        if (_values is not null) return;

        if (!File.Exists(_path))
        {
            _values = new Dictionary<string, string>();
            return;
        }

        byte[] plain;
        try
        {
            plain = EncryptedFile.Open(File.ReadAllBytes(_path), Magic);
        }
        catch (InvalidDataException)
        {
            throw new SettingsException($"Archivo de configuración inválido: {_path}");
        }
        catch (CryptographicException)
        {
            throw new SettingsException(
                $"No se puede descifrar la configuración ({_path}). Pertenece a otro usuario/instalación; bórrala para recrearla.");
        }

        _values = JsonSerializer.Deserialize(plain, SettingsJsonContext.Default.DictionaryStringString)
                  ?? new Dictionary<string, string>();
    }

    private void Save()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(_values!, SettingsJsonContext.Default.DictionaryStringString);
        EncryptedFile.WriteAtomic(_path, EncryptedFile.Seal(plain, Magic));
    }
}
