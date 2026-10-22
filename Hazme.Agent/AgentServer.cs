using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Como;
using Como.Settings;

namespace Hazme.Peer;

/// <summary>
/// <c>hazme-agent</c>: ejecutor efímero para que un hazme de otra máquina trabaje aquí. Sin IA ni configuración.
/// Una sola conexión (el puerto se cierra al autenticarse), un token de un solo uso mostrado en pantalla,
/// y caducidad por espera, inactividad y duración total. Todo lo que ejecuta se ve en esta terminal.
/// </summary>
internal static class AgentServer
{
    private const int MaxBadTokens = 3;

    public static async Task<int> RunAsync(string[] opts)
    {
        var port = 7700;
        var listen = "127.0.0.1";
        TimeSpan wait = TimeSpan.FromMinutes(10), idle = TimeSpan.FromMinutes(30), ttl = TimeSpan.FromMinutes(120);

        for (var i = 0; i < opts.Length; i++)
        {
            string? Next() => i + 1 < opts.Length ? opts[++i] : null;
            switch (opts[i])
            {
                case "-port" when int.TryParse(Next(), out var p) && p is >= 0 and <= 65535: port = p; break;
                case "-listen" when Next() is { } l: listen = l; break;
                case "-wait" when int.TryParse(Next(), out var w) && w > 0: wait = TimeSpan.FromMinutes(w); break;
                case "-idle" when int.TryParse(Next(), out var d) && d > 0: idle = TimeSpan.FromMinutes(d); break;
                case "-ttl" when int.TryParse(Next(), out var t) && t > 0: ttl = TimeSpan.FromMinutes(t); break;
                default:
                    ConsoleUi.Error($"Opción no válida: {opts[i]}. Admite: -port N, -listen IP, -wait MIN, -idle MIN, -ttl MIN (hazme-agent -h)");
                    return 1;
            }
        }

        if (!IPAddress.TryParse(listen, out var ip))
        {
            ConsoleUi.Error($"Dirección de escucha no válida: {listen}");
            return 1;
        }

        var token = PeerToken.Generate();
        TcpListener listener;
        try
        {
            listener = new TcpListener(ip, port);
            listener.Start(1);
        }
        catch (SocketException e)
        {
            ConsoleUi.Error($"No se puede escuchar en {ip}:{port}: {e.Message}");
            return 1;
        }
        var endpoint = (IPEndPoint)listener.LocalEndpoint;

        var shell = new ShellRunner(NoSettings.Instance, noStdin: true);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            // Ctrl-C: si hay un comando en curso es para él (recibe la señal con el terminal); si no, cierra el agente.
            if (!shell.HandleCtrlC()) stop.Cancel();
        };

        PrintBanner(endpoint, token, wait, idle, ttl, IPAddress.IsLoopback(ip));

        var started = DateTime.UtcNow;
        var deadline = started + ttl;

        // ---- espera de la única conexión ----
        SecureChannel? channel = null;
        TcpClient? client = null;
        var bad = 0;
        while (channel is null)
        {
            var left = started + wait - DateTime.UtcNow;
            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            acceptCts.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            TcpClient candidate;
            try
            {
                candidate = await listener.AcceptTcpClientAsync(acceptCts.Token);
            }
            catch (OperationCanceledException)
            {
                listener.Stop();
                Console.WriteLine();
                ConsoleUi.WriteLine(stop.IsCancellationRequested
                    ? "Agente cerrado."
                    : $"Nadie se conectó en {wait.TotalMinutes:0} min. Agente cerrado.", ConsoleColor.Yellow);
                return stop.IsCancellationRequested ? 130 : 0;
            }

            var who = candidate.Client.RemoteEndPoint?.ToString() ?? "?";
            var (ch, badToken) = await PeerHandshake.ServerAsync(candidate.GetStream(), token, stop.Token);
            if (ch is not null)
            {
                channel = ch;
                client = candidate;
                ConsoleUi.WriteLine($"✔ Conectado: {who}", ConsoleColor.Green);
                break;
            }

            candidate.Dispose();
            if (!badToken)
            {
                ConsoleUi.WriteLine($"  conexión descartada de {who} (no habla el protocolo)", ConsoleColor.DarkGray);
                continue;
            }
            bad++;
            ConsoleUi.WriteLine($"✖ Token incorrecto desde {who} ({bad}/{MaxBadTokens})", ConsoleColor.Red);
            if (bad >= MaxBadTokens)
            {
                listener.Stop();
                ConsoleUi.WriteLine($"{MaxBadTokens} tokens incorrectos: agente cerrado por seguridad.", ConsoleColor.Red);
                return 2;
            }
        }
        listener.Stop(); // ya no se aceptan más conexiones

        // ---- sesión ----
        var session = new Session(channel, shell, stop.Token);
        var reason = await session.RunAsync(idle, deadline);
        shell.Kill();
        await session.WaitIdleAsync();
        channel.Dispose();
        client?.Dispose();

        Console.WriteLine();
        ConsoleUi.WriteLine($"{reason} Agente finalizado.", ConsoleColor.Yellow);
        return 0;
    }

    private static void PrintBanner(IPEndPoint ep, string token, TimeSpan wait, TimeSpan idle, TimeSpan ttl, bool loopback)
    {
        Console.WriteLine();
        ConsoleUi.WriteLine("hazme-agent: ejecutor remoto temporal", ConsoleColor.Magenta);
        ConsoleUi.Write("  Escuchando en ", ConsoleColor.Gray);
        ConsoleUi.WriteLine($"{ep.Address}:{ep.Port}" + (loopback ? "  (solo esta máquina: usa un túnel ssh)" : ""), ConsoleColor.White);
        ConsoleUi.Write("  Token (cópialo al hazme de la otra máquina): ", ConsoleColor.Gray);
        ConsoleUi.WriteLine(PeerToken.Format(token), ConsoleColor.Cyan);
        ConsoleUi.WriteLine(
            $"  Una sola conexión · espera {wait.TotalMinutes:0} min · inactividad {idle.TotalMinutes:0} min · duración máxima {ttl.TotalMinutes:0} min",
            ConsoleColor.Gray);
        ConsoleUi.WriteLine("  Los comandos que lleguen se ejecutarán aquí con tus permisos y se mostrarán en esta terminal.", ConsoleColor.Gray);
        ConsoleUi.WriteLine("  Ctrl-C cierra el agente (o interrumpe el comando en curso).", ConsoleColor.Gray);
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            ConsoleUi.WriteLine("  ⚠ Estás como root: la otra máquina tendrá permisos de root aquí.", ConsoleColor.Red);
        if (!loopback)
            ConsoleUi.WriteLine("  ⚠ Escucha en la red: el tráfico va cifrado, pero cualquiera que alcance el puerto puede intentar conectar.", ConsoleColor.Yellow);
        Console.WriteLine();
    }

    private sealed class Session(SecureChannel channel, ShellRunner shell, CancellationToken stop)
    {
        private Task? _running;
        private DateTime _lastActivity = DateTime.UtcNow;

        private bool Busy => _running is { IsCompleted: false };

        public async Task WaitIdleAsync()
        {
            if (_running is not null)
                try { await _running.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ya se mató */ }
        }

        /// <summary>Atiende peticiones hasta que acabe la sesión. Devuelve el motivo.</summary>
        public async Task<string> RunAsync(TimeSpan idle, DateTime deadline)
        {
            var read = channel.ReceiveAsync(stop);
            while (true)
            {
                await Task.WhenAny(read, Task.Delay(1000, CancellationToken.None));
                if (stop.IsCancellationRequested) return "Cerrado por el usuario.";

                if (DateTime.UtcNow > deadline) return "Alcanzada la duración máxima.";
                if (!Busy && DateTime.UtcNow - _lastActivity > idle)
                    return $"Sin peticiones durante {idle.TotalMinutes:0} min.";
                if (!read.IsCompleted) continue;

                byte[]? data;
                try
                {
                    data = await read;
                }
                catch (CryptographicException)
                {
                    return "Mensaje alterado o inválido: conexión cerrada.";
                }
                catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException)
                {
                    return "Se perdió la conexión.";
                }
                catch (OperationCanceledException)
                {
                    return "Cerrado por el usuario.";
                }

                if (data is null) return "La otra máquina cerró la conexión.";
                read = channel.ReceiveAsync(stop);

                using var doc = JsonDocument.Parse(data);
                var msg = doc.RootElement;
                switch (PeerMsg.Str(msg, "t"))
                {
                    case "info":
                        await channel.SendAsync(PeerInfo.ForThisMachine().ToMessage(), stop);
                        break;
                    case "run":
                        if (Busy)
                        {
                            await SendDoneAsync(PeerMsg.Int(msg, "id"), new ShellResult(-1, "", "", false, "El agente ya está ejecutando otro comando."));
                            break;
                        }
                        _lastActivity = DateTime.UtcNow;
                        _running = RunCommandAsync(PeerMsg.Int(msg, "id"), PeerMsg.Str(msg, "command") ?? "", PeerMsg.Str(msg, "cwd"));
                        break;
                    case "cancel":
                        if (shell.Kill()) ConsoleUi.WriteLine("  ✖ interrumpido por la otra máquina", ConsoleColor.Red);
                        break;
                    case "bye":
                        return "La otra máquina terminó su trabajo.";
                }
            }
        }

        private async Task RunCommandAsync(int id, string command, string? cwd)
        {
            Console.WriteLine();
            ConsoleUi.Write("$ ", ConsoleColor.Green);
            ConsoleUi.WriteLine(command, ConsoleColor.Green);
            if (!string.IsNullOrWhiteSpace(cwd)) ConsoleUi.WriteLine($"  en {cwd}", ConsoleColor.DarkGray);

            ShellResult result;
            try
            {
                result = await shell.RunAsync(command, cwd, (text, isErr) =>
                {
                    ConsoleUi.Write(text, isErr ? ConsoleColor.DarkYellow : ConsoleColor.Gray, isErr);
                    try
                    {
                        channel.SendAsync(PeerMsg.Build(w =>
                        {
                            w.WriteString("t", "out");
                            w.WriteNumber("id", id);
                            w.WriteBoolean("err", isErr);
                            w.WriteString("d", text);
                        }), stop).GetAwaiter().GetResult();
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
                });
            }
            catch (Exception e)
            {
                result = new ShellResult(-1, "", "", false, $"{e.GetType().Name}: {e.Message}");
            }

            _lastActivity = DateTime.UtcNow;
            Console.WriteLine();
            try { await SendDoneAsync(id, result); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
        }

        private Task SendDoneAsync(int id, ShellResult r) => channel.SendAsync(PeerMsg.Build(w =>
        {
            w.WriteString("t", "done");
            w.WriteNumber("id", id);
            w.WriteNumber("exit", r.ExitCode);
            w.WriteBoolean("interrupted", r.Interrupted);
            w.WriteString("stdout", r.Stdout);
            w.WriteString("stderr", r.Stderr);
            if (r.StartError is not null) w.WriteString("startError", r.StartError);
            if (r.Warning is not null) w.WriteString("warning", r.Warning);
        }), stop);
    }
}

/// <summary>El agente no usa la configuración del equipo: no tiene claves ni conectores.</summary>
internal sealed class NoSettings : ISettings
{
    public static NoSettings Instance { get; } = new();
    public string? Get(string key) => null;
}
