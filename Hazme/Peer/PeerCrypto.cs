using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Hazme.Peer;

internal sealed class PeerException(string message) : Exception(message);

/// <summary>
/// Token de sesión del agente: 28 símbolos Crockford base32 (140 bits), generado al arrancar y de un solo uso.
/// Se escribe en grupos de 4 (K7QM-2XPD-...). Es secreto de alta entropía: no hace falta PAKE ni limitar PINs cortos.
/// </summary>
internal static class PeerToken
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 28;

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(Length);
        var chars = new char[Length];
        for (var i = 0; i < Length; i++) chars[i] = Alphabet[bytes[i] & 31]; // 256 es múltiplo de 32: uniforme
        return new string(chars);
    }

    public static string Format(string token) =>
        string.Join('-', Enumerable.Range(0, token.Length / 4).Select(i => token.Substring(i * 4, 4)));

    /// <summary>Quita separadores y espacios, pasa a mayúsculas y corrige O→0, I/L→1. Null si no es un token.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null) return null;
        var sb = new StringBuilder();
        foreach (var c0 in input)
        {
            if (c0 is '-' or ' ' or '\t' or '\r' or '\n') continue;
            var c = char.ToUpperInvariant(c0);
            c = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (!Alphabet.Contains(c)) return null;
            sb.Append(c);
        }
        return sb.Length == Length ? sb.ToString() : null;
    }
}

/// <summary>
/// Handshake con autenticación mutua por desafío-respuesta (HMAC-SHA256) sobre el token; el token nunca viaja.
/// De él se derivan (HKDF) una clave por dirección para el canal AES-256-GCM.
///
///   C→S  "HZP1" | nonceC(32)
///   S→C  nonceS(32) | HMAC(auth, "S"|nonceC|nonceS)     (el cliente comprueba que el agente conoce el token)
///   C→S  HMAC(auth, "C"|nonceC|nonceS)                  (el agente comprueba al cliente)
///   S→C  primer mensaje cifrado "ready"
/// </summary>
internal static class PeerHandshake
{
    private static readonly byte[] Magic = "HZP1"u8.ToArray();
    private const int NonceSize = 32, ProofSize = 32;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static byte[] Derive(byte[] ikm, byte[]? salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.ASCII.GetBytes(info));

    private static byte[] Proof(byte[] authKey, string role, byte[] nc, byte[] ns) =>
        HMACSHA256.HashData(authKey, Cat(Encoding.ASCII.GetBytes(role), nc, ns));

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Lado agente. <c>BadToken</c> solo si el cliente habló el protocolo pero falló la prueba.</summary>
    public static async Task<(SecureChannel? Channel, bool BadToken)> ServerAsync(Stream s, string token, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            var hello = await SecureChannel.ReadExactAsync(s, Magic.Length + NonceSize, limit.Token);
            if (!hello.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return (null, false);
            var nc = hello[Magic.Length..];

            var ikm = Encoding.ASCII.GetBytes(token);
            var authKey = Derive(ikm, null, "hazme-peer/v1 auth");
            var ns = RandomNumberGenerator.GetBytes(NonceSize);
            await s.WriteAsync(Cat(ns, Proof(authKey, "S", nc, ns)), limit.Token);

            var theirs = await SecureChannel.ReadExactAsync(s, ProofSize, limit.Token);
            if (!CryptographicOperations.FixedTimeEquals(theirs, Proof(authKey, "C", nc, ns))) return (null, true);

            var salt = Cat(nc, ns);
            var channel = new SecureChannel(s, Derive(ikm, salt, "hazme-peer/v1 s2c"), Derive(ikm, salt, "hazme-peer/v1 c2s"));
            await channel.SendAsync("""{"t":"ready"}"""u8.ToArray(), limit.Token);
            return (channel, false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or EndOfStreamException)
        {
            return (null, false); // cliente que no habla el protocolo, se cuelga o se va: no cuenta como token malo
        }
    }

    public static async Task<SecureChannel> ClientAsync(Stream s, string token, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            var ikm = Encoding.ASCII.GetBytes(token);
            var authKey = Derive(ikm, null, "hazme-peer/v1 auth");
            var nc = RandomNumberGenerator.GetBytes(NonceSize);
            await s.WriteAsync(Cat(Magic, nc), limit.Token);

            var reply = await SecureChannel.ReadExactAsync(s, NonceSize + ProofSize, limit.Token);
            var ns = reply[..NonceSize];

            // La prueba del cliente se envía SIEMPRE, antes de verificar la del agente: así un token equivocado
            // llega al agente como intento fallido y cuenta para su límite de 3. La prueba no revela el token.
            await s.WriteAsync(Proof(authKey, "C", nc, ns), limit.Token);
            if (!CryptographicOperations.FixedTimeEquals(reply.AsSpan(NonceSize), Proof(authKey, "S", nc, ns)))
                throw new PeerException("El agente no reconoce este token (token equivocado, o no es un agente de hazme).");

            var salt = Cat(nc, ns);
            var channel = new SecureChannel(s, Derive(ikm, salt, "hazme-peer/v1 c2s"), Derive(ikm, salt, "hazme-peer/v1 s2c"));
            var ready = await channel.ReceiveAsync(limit.Token);
            if (ready is null) throw new PeerException("El agente cerró la conexión: token rechazado o agente ya cerrado.");
            return channel;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or CryptographicException)
        {
            throw new PeerException("El agente cerró la conexión durante el acuerdo: token rechazado o agente ya cerrado.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PeerException("El agente no respondió al acuerdo de conexión a tiempo.");
        }
    }
}

/// <summary>
/// Canal de mensajes cifrado: cada trama es [longitud(4)] [AES-GCM(texto)+tag(16)]. El nonce es un contador de 64 bits
/// por dirección (implícito, no viaja): cualquier repetición, pérdida o alteración rompe el descifrado y cierra el canal.
/// </summary>
internal sealed class SecureChannel : IDisposable
{
    private const int MaxFrame = 8 * 1024 * 1024;
    private const int TagSize = 16;

    private readonly Stream _stream;
    private readonly AesGcm _send, _recv;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ulong _sendCounter, _recvCounter;

    public SecureChannel(Stream stream, byte[] sendKey, byte[] recvKey)
    {
        _stream = stream;
        _send = new AesGcm(sendKey, TagSize);
        _recv = new AesGcm(recvKey, TagSize);
    }

    private static byte[] Nonce(ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    public async Task SendAsync(byte[] plain, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var frame = new byte[4 + plain.Length + TagSize];
            BinaryPrimitives.WriteInt32BigEndian(frame, plain.Length + TagSize);
            var cipher = frame.AsSpan(4, plain.Length);
            var tag = frame.AsSpan(4 + plain.Length, TagSize);
            _send.Encrypt(Nonce(_sendCounter++), plain, cipher, tag);
            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Siguiente mensaje, o null si el otro lado cerró limpiamente. CryptographicException si fue alterado.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[4];
        var first = await _stream.ReadAsync(header.AsMemory(0, 1), ct);
        if (first == 0) return null;
        await ReadFullyAsync(_stream, header.AsMemory(1), ct);

        var len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len < TagSize || len > MaxFrame) throw new CryptographicException("Trama inválida.");
        var body = await ReadExactAsync(_stream, len, ct);

        var plain = new byte[len - TagSize];
        _recv.Decrypt(Nonce(_recvCounter++), body.AsSpan(0, plain.Length), body.AsSpan(plain.Length, TagSize), plain);
        return plain;
    }

    public static async Task<byte[]> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        await ReadFullyAsync(s, buf, ct);
        return buf;
    }

    private static async Task ReadFullyAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[read..], ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public void Dispose()
    {
        _send.Dispose();
        _recv.Dispose();
        _stream.Dispose();
    }
}
