using System.Net.Sockets;
using System.Text.Json;

namespace Hazme.Peer;

/// <summary>Un agente al que conectarse: <c>-peer id:host:puerto</c>.</summary>
internal sealed record PeerSpec(string Id, string Host, int Port)
{
    public static PeerSpec Parse(string spec)
    {
        var first = spec.IndexOf(':');
        var last = spec.LastIndexOf(':');
        if (first <= 0 || last <= first + 1 || last == spec.Length - 1
            || !int.TryParse(spec[(last + 1)..], out var port) || port is < 1 or > 65535)
            throw new PeerException($"Formato de -peer no válido: «{spec}». Usa id:host:puerto (p. ej. web2:127.0.0.1:7700).");

        var id = spec[..first];
        if (id.Length > 16 || id == "local" || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new PeerException($"Id de peer no válido: «{id}» (hasta 16 letras, números, - o _; «local» está reservado).");
        return new PeerSpec(id, spec[(first + 1)..last], port);
    }
}

/// <summary>Conexión a un agente remoto. Una petición a la vez. Tras perder la conexión no se reconecta (el token es de un solo uso).</summary>
internal sealed class PeerClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly SecureChannel _channel;
    private int _nextId;
    private volatile string? _lostReason;

    public PeerSpec Spec { get; }
    public PeerInfo Info { get; private set; }
    public bool Busy { get; private set; }
    public bool Connected => _lostReason is null;
    public string? LostReason => _lostReason;

    private PeerClient(PeerSpec spec, TcpClient tcp, SecureChannel channel, PeerInfo info)
    {
        Spec = spec;
        _tcp = tcp;
        _channel = channel;
        Info = info;
    }

    public static async Task<PeerClient> ConnectAsync(PeerSpec spec, string token, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            using var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectLimit.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await tcp.ConnectAsync(spec.Host, spec.Port, connectLimit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PeerException($"Tiempo agotado conectando con {spec.Host}:{spec.Port}.");
            }
            catch (SocketException e)
            {
                throw new PeerException(
                    $"No se pudo conectar con {spec.Host}:{spec.Port} ({e.SocketErrorCode}). ¿Está el agente en marcha y el túnel abierto?");
            }

            var channel = await PeerHandshake.ClientAsync(tcp.GetStream(), token, ct);
            try
            {
                await channel.SendAsync(PeerMsg.Build(w => w.WriteString("t", "info")), ct);
                var reply = await channel.ReceiveAsync(ct) ?? throw new PeerException("El agente cerró la conexión.");
                using var doc = JsonDocument.Parse(reply);
                return new PeerClient(spec, tcp, channel, PeerInfo.FromMessage(doc.RootElement));
            }
            catch
            {
                channel.Dispose();
                throw;
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private PeerException Lost(string reason)
    {
        _lostReason ??= reason;
        return new PeerException(
            $"Se perdió la conexión con el peer «{Spec.Id}» ({_lostReason}). No se reconecta sola: el usuario debe " +
            "arrancar un agente nuevo en esa máquina y relanzar hazme con -peer.");
    }

    /// <summary>Ejecuta un comando en el peer, entregando su salida en vivo. Si <paramref name="ct"/> se cancela, pide matarlo.</summary>
    public async Task<ShellResult> RunAsync(string command, string? cwd, Action<string, bool> onOutput, CancellationToken ct)
    {
        if (!Connected) throw Lost(_lostReason!);

        var id = ++_nextId;
        Busy = true;
        using var cancelReg = ct.Register(Kill);
        try
        {
            await _channel.SendAsync(PeerMsg.Build(w =>
            {
                w.WriteString("t", "run");
                w.WriteNumber("id", id);
                w.WriteString("command", command);
                if (!string.IsNullOrWhiteSpace(cwd)) w.WriteString("cwd", cwd);
            }), CancellationToken.None);

            while (true)
            {
                var data = await _channel.ReceiveAsync(CancellationToken.None);
                if (data is null) throw Lost("el agente cerró la conexión");

                using var doc = JsonDocument.Parse(data);
                var m = doc.RootElement;
                if (PeerMsg.Int(m, "id") != id) continue;

                switch (PeerMsg.Str(m, "t"))
                {
                    case "out":
                        onOutput(PeerMsg.Str(m, "d") ?? "", PeerMsg.Bool(m, "err"));
                        break;
                    case "done":
                        return new ShellResult(
                            PeerMsg.Int(m, "exit", -1), PeerMsg.Str(m, "stdout") ?? "", PeerMsg.Str(m, "stderr") ?? "",
                            PeerMsg.Bool(m, "interrupted"), PeerMsg.Str(m, "startError"), PeerMsg.Str(m, "warning"));
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or System.Security.Cryptography.CryptographicException
                                      or ObjectDisposedException)
        {
            throw Lost(e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Pide al agente matar el comando en curso (Ctrl-C / Esc). No espera respuesta.</summary>
    public void Kill()
    {
        if (!Connected) return;
        try
        {
            _ = _channel.SendAsync(PeerMsg.Build(w => w.WriteString("t", "cancel")), CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Connected)
        {
            try { _channel.SendAsync(PeerMsg.Build(w => w.WriteString("t", "bye")), CancellationToken.None).Wait(1000); }
            catch { /* nos vamos igualmente */ }
        }
        _channel.Dispose();
        _tcp.Dispose();
    }
}

/// <summary>Los peers conectados en esta ejecución, por id.</summary>
internal sealed class PeerSet : IDisposable
{
    private readonly Dictionary<string, PeerClient> _peers = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _peers.Count;
    public IEnumerable<PeerClient> All => _peers.Values;

    public void Add(PeerClient peer) => _peers[peer.Spec.Id] = peer;
    public PeerClient? Get(string id) => _peers.GetValueOrDefault(id);
    public bool Has(string id) => _peers.ContainsKey(id);
    public string Ids => string.Join(", ", _peers.Keys);

    /// <summary>Ctrl-C: si algún peer está ejecutando un comando, lo mata allí. False si no había ninguno.</summary>
    public bool HandleCtrlC()
    {
        var any = false;
        foreach (var p in _peers.Values.Where(p => p.Busy))
        {
            p.Kill();
            any = true;
        }
        return any;
    }

    public void Dispose()
    {
        foreach (var p in _peers.Values) p.Dispose();
        _peers.Clear();
    }
}
