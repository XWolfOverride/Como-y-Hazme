using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Como;
using Como.Connectors;
using Como.Settings;
using Hazme.Chat;

namespace Hazme;

/// <summary>
/// Bucle de trabajo: la IA propone herramientas (comandos, leer/listar/escribir archivos, preguntar),
/// se ejecutan, y el resultado vuelve a la IA hasta que termina, el usuario aborta o se agotan los pasos.
/// </summary>
internal sealed class JobRunner(
    IChatClient chat, SettingsStore settings, Interaction interaction, ShellRunner shell, CancellationToken ct)
{
    private const ConsoleColor NarrationColor = ConsoleColor.Yellow;
    private const ConsoleColor CommandColor = ConsoleColor.Green;
    private const ConsoleColor InfoColor = ConsoleColor.Cyan;
    private const int MaxFileChars = 64_000;
    private const int MaxListEntries = 500;

    private static readonly IReadOnlyList<ToolSpec> Tools =
    [
        new("run_command",
            "Run a shell command on the user's machine and return its exit code, stdout and stderr. " +
            "Each call uses a fresh shell; 'cd' does not persist between calls.",
            """{"type":"object","properties":{"command":{"type":"string","description":"The command line to run"},"cwd":{"type":"string","description":"Working directory (optional, defaults to the current directory)"}},"required":["command"]}"""),
        new("read_file",
            "Read a text file and return its contents (truncated if very large).",
            """{"type":"object","properties":{"path":{"type":"string","description":"File path"}},"required":["path"]}"""),
        new("list_dir",
            "List the entries of a directory. Directories end with '/'.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Directory path (defaults to current directory)"},"recursive":{"type":"boolean","description":"Include subdirectories (max depth 3)"}},"required":[]}"""),
        new("write_file",
            "Create or overwrite a text file with the given content (parent directories are created).",
            """{"type":"object","properties":{"path":{"type":"string","description":"File path"},"content":{"type":"string","description":"Full file content"}},"required":["path","content"]}"""),
        new("ask_user",
            "Ask the user a question and wait for the answer. Use it when you need a decision or after a command was interrupted.",
            """{"type":"object","properties":{"question":{"type":"string","description":"The question"}},"required":["question"]}"""),
    ];

    public async Task<int> RunAsync(string task)
    {
        var maxSteps = Math.Max(1, KnownKeys.IntValue(settings, HazmeKeys.MaxSteps, 30));
        var request = new ChatRequest(BuildSystemPrompt(), Tools);
        var history = new List<ChatMessage> { new(ChatRole.User, task) };
        var lineStart = true;

        for (var step = 0; step < maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            var turn = await chat.ChatAsync(history, request, chunk =>
            {
                ConsoleUi.Write(chunk, NarrationColor);
                lineStart = chunk.EndsWith('\n');
            }, ct);

            if (!lineStart)
            {
                Console.WriteLine();
                lineStart = true;
            }

            history.Add(new ChatMessage(ChatRole.Assistant, RawContentJson: turn.RawContentJson));

            if (turn.ToolCalls.Count == 0)
            {
                // Si el usuario dejó una nota justo al final, se atiende en vez de terminar.
                if (interaction.TakeNotes() is { } late)
                {
                    history.Add(new ChatMessage(ChatRole.User, late));
                    continue;
                }
                return 0;
            }

            var results = new List<ToolResult>();
            foreach (var call in turn.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await ExecuteAsync(call));
            }

            history.Add(new ChatMessage(ChatRole.User, interaction.TakeNotes(), results));
        }

        ConsoleUi.WriteLine($"Límite de {maxSteps} pasos alcanzado; trabajo detenido.", ConsoleColor.Red, error: true);
        return 1;
    }

    private async Task<ToolResult> ExecuteAsync(ToolCall call)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(call.ArgumentsJson);
        }
        catch (JsonException)
        {
            return Fail(call, "Invalid arguments JSON.");
        }

        using (doc)
        {
            var a = doc.RootElement;
            switch (call.Name)
            {
                case "run_command":
                    return await RunCommandAsync(call, Str(a, "command"), Str(a, "cwd"));
                case "read_file":
                    return ReadFile(call, Str(a, "path"));
                case "list_dir":
                    return ListDir(call, Str(a, "path") ?? ".", a.TryGetProperty("recursive", out var r) && r.ValueKind == JsonValueKind.True);
                case "write_file":
                    return await WriteFileAsync(call, Str(a, "path"), Str(a, "content"));
                case "ask_user":
                    return AskUser(call, Str(a, "question"));
                default:
                    return Fail(call, $"Unknown tool: {call.Name}");
            }
        }
    }

    // ---------- herramientas ----------

    private async Task<ToolResult> RunCommandAsync(ToolCall call, string? command, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(command)) return Fail(call, "Missing 'command'.");

        Console.WriteLine();
        ConsoleUi.Write("$ ", CommandColor);
        ConsoleUi.WriteLine(command, CommandColor);
        if (!string.IsNullOrWhiteSpace(cwd)) ConsoleUi.WriteLine($"  en {cwd}", ConsoleColor.DarkGray);

        await GraceAsync();

        var res = await shell.RunAsync(command, cwd);
        if (!Console.IsOutputRedirected || res.Stdout.Length + res.Stderr.Length > 0) Console.WriteLine();

        if (res.StartError is not null)
            return Fail(call, $"Could not start the command: {res.StartError}");

        var sb = new StringBuilder();
        sb.Append("exit_code: ").Append(res.ExitCode).Append('\n');
        if (res.Interrupted)
            sb.Append("interrupted_by_user: true (the user pressed Ctrl-C while the command was running; ask the user whether to continue)\n");
        sb.Append("stdout:\n").Append(res.Stdout.Length == 0 ? "(empty)" : res.Stdout).Append('\n');
        sb.Append("stderr:\n").Append(res.Stderr.Length == 0 ? "(empty)" : res.Stderr);

        if (res.Interrupted)
            ConsoleUi.WriteLine("  interrumpido por el usuario", ConsoleColor.Red);
        else if (res.ExitCode != 0)
            ConsoleUi.WriteLine($"  código de salida {res.ExitCode}", ConsoleColor.Red);

        return new ToolResult(call.Id, call.Name, sb.ToString(), IsError: res.ExitCode != 0 || res.Interrupted);
    }

    private ToolResult ReadFile(ToolCall call, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Fail(call, "Missing 'path'.");
        ConsoleUi.WriteLine($"» leer {path}", InfoColor);
        try
        {
            var text = File.ReadAllText(path);
            if (text.Length > MaxFileChars)
                text = text[..MaxFileChars] + $"\n[... truncado: {text.Length - MaxFileChars} caracteres más ...]";
            return new ToolResult(call.Id, call.Name, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(call, e.Message);
        }
    }

    private ToolResult ListDir(ToolCall call, string path, bool recursive)
    {
        ConsoleUi.WriteLine($"» listar {path}{(recursive ? " (recursivo)" : "")}", InfoColor);
        try
        {
            var root = Path.GetFullPath(path);
            var sb = new StringBuilder();
            var count = 0;

            void Walk(string dir, int depth)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (count++ >= MaxListEntries) return;
                    var isDir = Directory.Exists(entry);
                    sb.Append(Path.GetRelativePath(root, entry)).Append(isDir ? "/" : "").Append('\n');
                    if (recursive && isDir && depth < 3) Walk(entry, depth + 1);
                }
            }

            Walk(root, 1);
            if (count > MaxListEntries) sb.Append($"[... lista truncada a {MaxListEntries} entradas ...]\n");
            return new ToolResult(call.Id, call.Name, sb.Length == 0 ? "(empty directory)" : sb.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(call, e.Message);
        }
    }

    private async Task<ToolResult> WriteFileAsync(ToolCall call, string? path, string? content)
    {
        if (string.IsNullOrWhiteSpace(path)) return Fail(call, "Missing 'path'.");
        content ??= "";

        var lines = content.Split('\n').Length;
        Console.WriteLine();
        ConsoleUi.Write("+ escribir ", CommandColor);
        ConsoleUi.WriteLine($"{path} ({lines} líneas, {content.Length} caracteres)", CommandColor);
        foreach (var l in content.Split('\n').Take(5))
            ConsoleUi.WriteLine("  | " + (l.Length > 100 ? l[..100] + "…" : l.TrimEnd('\r')), ConsoleColor.DarkGray);
        if (lines > 5) ConsoleUi.WriteLine("  | …", ConsoleColor.DarkGray);

        await GraceAsync();

        try
        {
            var full = Path.GetFullPath(path);
            var existed = File.Exists(full);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct);
            return new ToolResult(call.Id, call.Name, existed ? $"Overwrote {path}" : $"Created {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(call, e.Message);
        }
    }

    private ToolResult AskUser(ToolCall call, string? question)
    {
        Console.WriteLine();
        ConsoleUi.WriteLine("? " + (question ?? ""), NarrationColor);
        ConsoleUi.Write("› ", ConsoleColor.White);

        var answer = interaction.ReadLine();
        ct.ThrowIfCancellationRequested();
        return new ToolResult(call.Id, call.Name, string.IsNullOrWhiteSpace(answer) ? "(no answer)" : answer);
    }

    // ---------- tiempo de gracia ----------

    /// <summary>
    /// Pausa visible antes de actuar. Esc (gestionado por Interaction) cancela el trabajo y esto lanza
    /// OperationCanceledException. Sin terminal interactivo no se espera.
    /// </summary>
    private async Task GraceAsync()
    {
        var seconds = KnownKeys.IntValue(settings, HazmeKeys.GraceSeconds, 3);
        if (seconds <= 0 || !interaction.Interactive) return;

        var show = !Console.IsOutputRedirected;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var left = end - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) break;
            if (show)
                ConsoleUi.Write($"\r  ejecutando en {Math.Ceiling(left.TotalSeconds)}s · Esc para abortar   ", ConsoleColor.DarkGray);
            await Task.Delay(100, ct);
        }
        if (show) Console.Write("\r" + new string(' ', 48) + "\r");
        ct.ThrowIfCancellationRequested();
    }

    // ---------- utilidades ----------

    private static ToolResult Fail(ToolCall call, string message) =>
        new(call.Id, call.Name, message, IsError: true);

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private string BuildSystemPrompt()
    {
        var shellName = OperatingSystem.IsWindows()
            ? "PowerShell"
            : Path.GetFileName(Environment.GetEnvironmentVariable("SHELL") ?? "sh");
        return $"""
            You are "hazme", an agent that performs tasks on the user's computer from the command line, using tools.
            Environment: {RuntimeInformation.OSDescription}; shell: {shellName}; user: {Environment.UserName}; current directory: {Environment.CurrentDirectory}.

            Rules:
            - Work step by step. Before acting, say in one short sentence what you are about to do. Keep all text brief and plain (no markdown).
            - Inspect before changing: use list_dir/read_file or read-only commands to understand the situation first.
            - Every command is shown to the user, who may abort the whole job at any time with Esc. Prefer non-interactive flags (e.g. -y) and avoid unnecessary destructive operations.
            - Each run_command is a fresh shell: use `cd dir && cmd` or the cwd parameter, never rely on a previous cd.
            - Check each result (exit code, stderr). If a command fails, diagnose and fix it; do not repeat the same failing command blindly.
            - If a result says interrupted_by_user, stop and use ask_user to ask whether to continue.
            - Messages starting with "[Nota del usuario mientras trabajabas]" are guidance the user typed while you were working: take them into account.
            - Use ask_user only when you truly need a decision. When the task is complete, reply with a short summary and no tool calls.
            - Answer in the language of the user's request.
            """;
    }
}

