using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Como;
using Como.Settings;

namespace Hazme;

internal sealed record ShellResult(
    int ExitCode, string Stdout, string Stderr, bool Interrupted, string? StartError = null, string? Warning = null);

/// <summary>
/// Lanza comandos en el shell del sistema. El comando hereda la entrada del terminal (puede ser
/// interactivo); stdout/stderr se muestran en vivo y se capturan (truncados) para la IA.
/// </summary>
internal sealed class ShellRunner(ISettings settings, bool noStdin = false)
{
    private readonly object _lock = new();
    private Process? _running;
    private int _ctrlCs;

    /// <summary>
    /// Ctrl-C mientras corre un comando: la primera vez lo recibe el propio comando (mismo grupo de
    /// proceso); la segunda mata su árbol de procesos. Devuelve false si no hay comando en curso.
    /// </summary>
    public bool HandleCtrlC()
    {
        lock (_lock)
        {
            if (_running is null) return false;
            if (++_ctrlCs >= 2)
            {
                try { _running.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            }
            return true;
        }
    }

    /// <summary>
    /// <paramref name="onOutput"/> recibe la salida en vivo (texto, esEstándarError); sin él se escribe en la consola.
    /// </summary>
    /// <summary>Mata ya el árbol de procesos del comando en curso (cancelación remota). False si no hay ninguno.</summary>
    public bool Kill()
    {
        lock (_lock)
        {
            if (_running is null) return false;
            _ctrlCs = Math.Max(_ctrlCs, 2);
            try { _running.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            return true;
        }
    }

    public async Task<ShellResult> RunAsync(string command, string? cwd, Action<string, bool>? onOutput = null)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = noStdin,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd,
        };

        var custom = KnownKeys.Value(settings, HazmeKeys.Shell);
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = string.IsNullOrWhiteSpace(custom) ? "powershell.exe" : custom;
            var name = Path.GetFileNameWithoutExtension(psi.FileName).ToLowerInvariant();
            if (name is "powershell" or "pwsh")
            {
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);
            }
            else
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
        }
        else
        {
            psi.FileName = !string.IsNullOrWhiteSpace(custom) ? custom
                : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        Process p;
        try
        {
            lock (_lock)
            {
                p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar el proceso.");
                _running = p;
                _ctrlCs = 0;
            }
            // Sin nadie en el terminal (agente remoto): el comando recibe fin de entrada en vez de quedarse esperando.
            if (noStdin) p.StandardInput.Close();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or DirectoryNotFoundException)
        {
            return new ShellResult(-1, "", "", false,
                $"{e.GetType().Name}: {e.Message} (shell: {psi.FileName}, working directory: {psi.WorkingDirectory})");
        }

        string? warning = null;
        var outCap = new Capture();
        var errCap = new Capture();
        var pumps = Task.WhenAll(
            Pump(p.StandardOutput, outCap, ConsoleColor.Gray, error: false, onOutput),
            Pump(p.StandardError, errCap, ConsoleColor.DarkYellow, error: true, onOutput));

        try
        {
            await p.WaitForExitAsync();
            // Un nieto puede mantener abiertas las tuberías: no esperamos indefinidamente.
            try { await pumps.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                warning = "The command ended but its output pipes stayed open for more than 2 s (a background child process still holds them); the captured stdout/stderr may be incomplete.";
            }
        }
        finally
        {
            lock (_lock) _running = null;
        }

        var interrupted = _ctrlCs > 0;
        var exit = p.ExitCode;
        p.Dispose();
        return new ShellResult(exit, outCap.ToString(), errCap.ToString(), interrupted, Warning: warning);
    }

    private static async Task Pump(
        StreamReader reader, Capture capture, ConsoleColor color, bool error, Action<string, bool>? onOutput)
    {
        var buf = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
        {
            capture.Append(buf.AsSpan(0, n));
            var chunk = new string(buf, 0, n);
            if (onOutput is null) ConsoleUi.Write(chunk, color, error);
            else onOutput(chunk, error);
        }
    }

    /// <summary>Conserva el principio y el final de la salida y omite el medio.</summary>
    private sealed class Capture
    {
        private const int Head = 12_000, Tail = 12_000;
        private readonly StringBuilder _head = new(), _tail = new();
        private readonly object _l = new();
        private int _dropped;

        public void Append(ReadOnlySpan<char> s)
        {
            lock (_l)
            {
                if (_head.Length < Head)
                {
                    var take = Math.Min(Head - _head.Length, s.Length);
                    _head.Append(s[..take]);
                    s = s[take..];
                }
                if (s.Length == 0) return;
                _tail.Append(s);
                if (_tail.Length > Tail * 2) Trim();
            }
        }

        private void Trim()
        {
            var cut = _tail.Length - Tail;
            _tail.Remove(0, cut);
            _dropped += cut;
        }

        public override string ToString()
        {
            lock (_l)
            {
                if (_tail.Length > Tail) Trim();
                return _dropped == 0
                    ? _head.ToString() + _tail
                    : _head + $"\n[... {_dropped} caracteres omitidos ...]\n" + _tail;
            }
        }
    }
}

