using System.Text;
using Como;

namespace Hazme;

/// <summary>
/// Teclado durante el trabajo: Esc aborta el trabajo; cualquier texto + Enter es una nota
/// que se entrega a la IA en el siguiente paso. Se pausa mientras un comando es dueño del terminal.
/// </summary>
internal sealed class Interaction
{
    private readonly CancellationTokenSource _job;
    private readonly object _gate = new();
    private readonly object _notesLock = new();
    private readonly Queue<string> _notes = new();
    private readonly StringBuilder _line = new();
    private bool _paused;
    private volatile bool _stop;

    public Interaction(CancellationTokenSource job)
    {
        _job = job;
        Interactive = !Console.IsInputRedirected;
    }

    public bool Interactive { get; }

    public void Start()
    {
        if (!Interactive) return;
        new Thread(Loop) { IsBackground = true, Name = "hazme-keys" }.Start();
    }

    public void Stop() => _stop = true;

    private void Loop()
    {
        while (!_stop)
        {
            lock (_gate)
            {
                if (!_paused)
                {
                    try
                    {
                        while (Console.KeyAvailable) Handle(Console.ReadKey(true));
                    }
                    catch (InvalidOperationException)
                    {
                        return; // la entrada dejó de ser un terminal
                    }
                }
            }
            Thread.Sleep(25);
        }
    }

    private void Handle(ConsoleKeyInfo k)
    {
        switch (k.Key)
        {
            case ConsoleKey.Escape:
                _job.Cancel();
                return;

            case ConsoleKey.Enter:
                if (_line.Length == 0) return;
                lock (_notesLock) _notes.Enqueue(_line.ToString());
                _line.Clear();
                Console.WriteLine();
                ConsoleUi.WriteLine("  nota en cola: se la paso a la IA en el próximo paso", ConsoleColor.DarkGray);
                return;

            case ConsoleKey.Backspace:
                if (_line.Length > 0)
                {
                    _line.Length--;
                    Console.Write("\b \b");
                }
                return;
        }

        if (char.IsControl(k.KeyChar)) return;
        if (_line.Length == 0) ConsoleUi.Write("\n✎ ", ConsoleColor.Cyan);
        _line.Append(k.KeyChar);
        ConsoleUi.Write(k.KeyChar.ToString(), ConsoleColor.Cyan);
    }

    /// <summary>Notas pendientes del usuario, formateadas para la IA (o null si no hay).</summary>
    public string? TakeNotes()
    {
        lock (_notesLock)
        {
            if (_notes.Count == 0) return null;
            var text = "[Nota del usuario mientras trabajabas]\n" + string.Join('\n', _notes);
            _notes.Clear();
            return text;
        }
    }

    public IDisposable Pause()
    {
        lock (_gate) _paused = true;
        return new Resume(this);
    }

    private sealed class Resume(Interaction owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._paused = false;
        }
    }

    /// <summary>
    /// Espera una tecla que esté en <paramref name="valid"/> (sin distinguir mayúsculas).
    /// Esc aborta el trabajo y devuelve null; también null si no hay terminal interactivo.
    /// </summary>
    public char? ReadChoice(string valid)
    {
        if (!Interactive) return null;

        using var _ = Pause();
        while (true)
        {
            if (_job.IsCancellationRequested) return null;
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(25);
                continue;
            }

            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Escape)
            {
                _job.Cancel();
                return null;
            }
            var c = char.ToLowerInvariant(k.KeyChar);
            if (valid.Contains(c)) return c;
        }
    }

    /// <summary>Lee una línea del usuario. Devuelve null si pulsa Esc, Ctrl-C o no hay entrada.</summary>
    public string? ReadLine(bool escCancelsJob = true)
    {
        if (!Interactive) return Console.ReadLine();

        using var _ = Pause();
        var sb = new StringBuilder();
        while (true)
        {
            if (_job.IsCancellationRequested) return null;
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(25);
                continue;
            }

            var k = Console.ReadKey(true);
            switch (k.Key)
            {
                case ConsoleKey.Escape:
                    if (escCancelsJob) _job.Cancel();
                    Console.WriteLine();
                    return null;
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return sb.ToString();
                case ConsoleKey.Backspace:
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
            }

            if (char.IsControl(k.KeyChar)) continue;
            sb.Append(k.KeyChar);
            ConsoleUi.Write(k.KeyChar.ToString(), ConsoleColor.White);
        }
    }
}
