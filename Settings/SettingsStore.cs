using System.Security.Cryptography;
using System.Text;
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
/// </summary>
public sealed class SettingsStore : ISettings
{
    private static readonly byte[] Magic = "COM1"u8.ToArray();
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16;
    private const int HeaderSize = 4 + SaltSize + NonceSize + TagSize;

    private readonly string _path;
    private Dictionary<string, string>? _values;
    private byte[]? _salt;

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

        var data = File.ReadAllBytes(_path);
        if (data.Length < HeaderSize || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new SettingsException($"Archivo de configuración inválido: {_path}");

        var salt = data.AsSpan(4, SaltSize);
        var nonce = data.AsSpan(4 + SaltSize, NonceSize);
        var tag = data.AsSpan(4 + SaltSize + NonceSize, TagSize);
        var cipher = data.AsSpan(HeaderSize);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(DeriveKey(salt), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            throw new SettingsException(
                $"No se puede descifrar la configuración ({_path}). Pertenece a otro usuario/instalación; bórrala para recrearla.");
        }

        _salt = salt.ToArray();
        _values = JsonSerializer.Deserialize(plain, SettingsJsonContext.Default.DictionaryStringString)
                  ?? new Dictionary<string, string>();
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _salt ??= RandomNumberGenerator.GetBytes(SaltSize);
        var plain = JsonSerializer.SerializeToUtf8Bytes(_values!, SettingsJsonContext.Default.DictionaryStringString);

        var output = new byte[HeaderSize + plain.Length];
        Magic.CopyTo(output, 0);
        _salt.CopyTo(output, 4);
        var nonce = output.AsSpan(4 + SaltSize, NonceSize);
        var tag = output.AsSpan(4 + SaltSize + NonceSize, TagSize);
        RandomNumberGenerator.Fill(nonce);

        using (var aes = new AesGcm(DeriveKey(_salt), TagSize))
            aes.Encrypt(nonce, plain, output.AsSpan(HeaderSize, plain.Length), tag);

        // Escritura atómica: temporal + reemplazo.
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, output);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _path, overwrite: true);
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> salt)
    {
        var context = Encoding.UTF8.GetBytes(
            $"como/v1\0{Environment.UserName}\0{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");
        var input = new byte[salt.Length + context.Length];
        salt.CopyTo(input);
        context.CopyTo(input, salt.Length);
        return SHA256.HashData(input);
    }
}
