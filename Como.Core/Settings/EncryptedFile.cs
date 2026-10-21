using System.Security.Cryptography;
using System.Text;

namespace Como.Settings;

/// <summary>
/// Cifrado de archivos de la carpeta del usuario: "magic" | salt(16) | nonce(12) | tag(16) | ciphertext (AES-256-GCM).
/// La clave se deriva con SHA-256 de (salt aleatorio del archivo + usuario + carpeta personal): no hay ningún
/// secreto embebido en el binario. Protege de lecturas casuales, copias de seguridad y otros usuarios; no de
/// código que ya se ejecute con el mismo usuario.
/// </summary>
public static class EncryptedFile
{
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16;
    private const int HeaderSize = SaltSize + NonceSize + TagSize;

    public static byte[] Seal(byte[] plain, byte[] magic)
    {
        var output = new byte[magic.Length + HeaderSize + plain.Length];
        magic.CopyTo(output, 0);
        var salt = output.AsSpan(magic.Length, SaltSize);
        var nonce = output.AsSpan(magic.Length + SaltSize, NonceSize);
        var tag = output.AsSpan(magic.Length + SaltSize + NonceSize, TagSize);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(DeriveKey(salt), TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(magic.Length + HeaderSize, plain.Length), tag);
        return output;
    }

    /// <summary>Descifra. <see cref="InvalidDataException"/> si no es del formato; <see cref="CryptographicException"/> si no se puede descifrar.</summary>
    public static byte[] Open(byte[] data, byte[] magic)
    {
        if (data.Length < magic.Length + HeaderSize || !data.AsSpan(0, magic.Length).SequenceEqual(magic))
            throw new InvalidDataException("Formato de archivo cifrado inválido.");

        var salt = data.AsSpan(magic.Length, SaltSize);
        var nonce = data.AsSpan(magic.Length + SaltSize, NonceSize);
        var tag = data.AsSpan(magic.Length + SaltSize + NonceSize, TagSize);
        var cipher = data.AsSpan(magic.Length + HeaderSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(DeriveKey(salt), TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    /// <summary>Escritura atómica (temporal + reemplazo), con permisos solo para el usuario en Unix.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
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
