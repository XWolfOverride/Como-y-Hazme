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
    IChatClient chat, SettingsStore settings, Interaction interaction, ShellRunner shell,
    bool confirmAllChanges, CancellationToken ct)
{
    private const ConsoleColor NarrationColor = ConsoleColor.Yellow;
    private const ConsoleColor CommandColor = ConsoleColor.Green;
    private const ConsoleColor InfoColor = ConsoleColor.Cyan;

    private static readonly IReadOnlyList<ToolSpec> Tools =
    [
        new("run_command",
            "Run a shell command on the user's machine and return its exit code, stdout and stderr. " +
            "Each call uses a fresh shell; 'cd' does not persist between calls.",
            """{"type":"object","properties":{"command":{"type":"string","description":"The command line to run"},"cwd":{"type":"string","description":"Working directory (optional, defaults to the current directory)"}},"required":["command"]}"""),
        new("read_file",
            "Read a text file and return its contents (truncated if very large). Use start_line/max_lines to read a section of a big file.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File path"},"start_line":{"type":"integer","description":"First line to read (1-based, optional)"},"max_lines":{"type":"integer","description":"Maximum number of lines to return (optional)"}},"required":["path"]}"""),
        new("list_dir",
            "List a directory as a tree (directories first, files with size). Use max_depth to descend into subdirectories.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Directory path (defaults to current directory)"},"max_depth":{"type":"integer","description":"Levels to show, 1-6 (default 1 = only the directory's direct entries)"}},"required":[]}"""),
        new("stat_path",
            "Check whether a file or directory exists and return its type, size and modification time.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Path to check"}},"required":["path"]}"""),
        new("write_file",
            "Create or overwrite a text file with the given full content (parent directories are created). " +
            "For critical, configuration or system files use propose_change instead.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File path"},"content":{"type":"string","description":"Full file content"}},"required":["path","content"]}"""),
        new("propose_change",
            "Show the user a diff of a file change and let them apply it, discard it or ask for a revision. " +
            "Use it for critical changes, configuration files, system files that could cause problems, or whenever the user asked to confirm changes. " +
            "Provide the complete new content of the file.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File path"},"content":{"type":"string","description":"Complete new content of the file"},"summary":{"type":"string","description":"One short sentence explaining the change and why"}},"required":["path","content"]}"""),
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
                    return ReadFile(call, Str(a, "path"), Int(a, "start_line"), Int(a, "max_lines"));
                case "list_dir":
                    return ListDir(call, Str(a, "path") ?? ".", Int(a, "max_depth") ?? 1);
                case "stat_path":
                    return StatPath(call, Str(a, "path"));
                case "write_file":
                    return await WriteFileAsync(call, Str(a, "path"), Str(a, "content"));
                case "propose_change":
                    return await ProposeChangeAsync(call, Str(a, "path"), Str(a, "content"), Str(a, "summary"));
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

    private ToolResult ReadFile(ToolCall call, string? path, int? startLine, int? maxLines)
    {
        if (string.IsNullOrWhiteSpace(path)) return Fail(call, "Missing 'path'.");
        ConsoleUi.WriteLine($"» leer {path}", InfoColor);
        try
        {
            var full = FileTools.Resolve(path);
            if (!File.Exists(full)) return Fail(call, $"File not found: {path}");
            if (FileTools.LooksBinary(full))
                return Fail(call, $"Binary file ({FileTools.FormatSize(new FileInfo(full).Length)}); not shown.");

            var text = File.ReadAllText(full);
            if (startLine is > 1 || maxLines is > 0)
            {
                var lines = TextDiff.SplitLines(text);
                var from = Math.Clamp((startLine ?? 1) - 1, 0, lines.Length);
                var count = maxLines is > 0 ? Math.Min(maxLines.Value, lines.Length - from) : lines.Length - from;
                text = string.Join('\n', lines.Skip(from).Take(count))
                       + $"\n[líneas {from + 1}-{from + count} de {lines.Length}]";
            }
            if (text.Length > FileTools.MaxFileChars)
                text = text[..FileTools.MaxFileChars]
                       + $"\n[... truncado: {text.Length - FileTools.MaxFileChars} caracteres más; usa start_line/max_lines ...]";
            return new ToolResult(call.Id, call.Name, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(call, e.Message);
        }
    }

    private ToolResult ListDir(ToolCall call, string path, int maxDepth)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 6);
        ConsoleUi.WriteLine($"» listar {path}{(maxDepth > 1 ? $" (profundidad {maxDepth})" : "")}", InfoColor);
        try
        {
            var root = FileTools.Resolve(path);
            if (!Directory.Exists(root)) return Fail(call, $"Directory not found: {path}");
            return new ToolResult(call.Id, call.Name, FileTools.ListTree(root, maxDepth));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(call, e.Message);
        }
    }

    private ToolResult StatPath(ToolCall call, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Fail(call, "Missing 'path'.");
        ConsoleUi.WriteLine($"» comprobar {path}", InfoColor);
        try
        {
            var full = FileTools.Resolve(path);
            string text;
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                text = $"exists: true\ntype: file\nsize: {fi.Length} bytes ({FileTools.FormatSize(fi.Length)})\nmodified: {fi.LastWriteTimeUtc:u}\nfull_path: {full}";
            }
            else if (Directory.Exists(full))
            {
                text = $"exists: true\ntype: directory\nmodified: {Directory.GetLastWriteTimeUtc(full):u}\nfull_path: {full}";
            }
            else
            {
                text = $"exists: false\nfull_path: {full}";
            }
            return new ToolResult(call.Id, call.Name, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(call, e.Message);
        }
    }

    /// <summary>
    /// Muestra el cambio como diff y deja al usuario aplicarlo, descartarlo o pedir una revisión.
    /// Esc aborta el trabajo.
    /// </summary>
    private async Task<ToolResult> ProposeChangeAsync(ToolCall call, string? path, string? content, string? summary)
    {
        if (string.IsNullOrWhiteSpace(path)) return Fail(call, "Missing 'path'.");
        content ??= "";

        string full;
        string? current = null;
        try
        {
            full = FileTools.Resolve(path);
            if (Directory.Exists(full)) return Fail(call, $"{path} is a directory.");
            if (File.Exists(full))
            {
                if (new FileInfo(full).Length > 2_000_000 || FileTools.LooksBinary(full))
                    return Fail(call, "File is binary or too large to show a diff; use run_command instead.");
                current = File.ReadAllText(full);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(call, e.Message);
        }

        // Conserva los saltos de línea CRLF de un archivo que ya los usaba.
        if (current is not null && current.Contains("\r\n") && !content.Contains('\r'))
            content = content.Replace("\n", "\r\n");

        Console.WriteLine();
        ConsoleUi.Write("± cambio propuesto: ", ConsoleColor.Magenta);
        ConsoleUi.WriteLine(path + (current is null ? " (archivo nuevo)" : ""), ConsoleColor.Magenta);
        if (!string.IsNullOrWhiteSpace(summary)) ConsoleUi.WriteLine("  " + summary, ConsoleColor.DarkGray);

        var oldLines = current is null ? [] : TextDiff.SplitLines(current);
        var newLines = TextDiff.SplitLines(content);
        var diff = TextDiff.Compute(oldLines, newLines);
        if (diff is null)
        {
            ConsoleUi.WriteLine(
                $"  (cambio demasiado grande para comparar: {oldLines.Length} → {newLines.Length} líneas)", ConsoleColor.DarkGray);
        }
        else if (diff.All(d => d.Op == TextDiff.Op.Equal) && current == content)
        {
            return new ToolResult(call.Id, call.Name, "No changes: the file already has this content.");
        }
        else
        {
            var (added, removed) = TextDiff.Count(diff);
            ConsoleUi.WriteLine($"  +{added} -{removed} líneas", ConsoleColor.DarkGray);
            if (!TextDiff.Print(diff))
                ConsoleUi.WriteLine("  … (diff truncado en pantalla)", ConsoleColor.DarkGray);
        }

        if (!interaction.Interactive)
            return Fail(call, "Cannot ask for confirmation without an interactive terminal; the change was not applied.");

        while (true)
        {
            Console.WriteLine();
            ConsoleUi.Write("[A]", ConsoleColor.Green);
            ConsoleUi.Write("plicar  ", ConsoleColor.Gray);
            ConsoleUi.Write("[D]", ConsoleColor.Red);
            ConsoleUi.Write("escartar  ", ConsoleColor.Gray);
            ConsoleUi.Write("[I]", ConsoleColor.Cyan);
            ConsoleUi.WriteLine("nstrucciones   (Esc aborta el trabajo)", ConsoleColor.Gray);

            var choice = interaction.ReadChoice("adi");
            ct.ThrowIfCancellationRequested();

            switch (choice)
            {
                case 'a':
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        return Fail(call, e.Message);
                    }
                    ConsoleUi.WriteLine("  ✔ aplicado", ConsoleColor.Green);
                    return new ToolResult(call.Id, call.Name, current is null ? $"Created {path}" : $"Applied change to {path}");

                case 'd':
                    ConsoleUi.WriteLine("  ✖ descartado", ConsoleColor.Red);
                    return new ToolResult(call.Id, call.Name,
                        "The user DISCARDED this change; the file was not modified. Do not retry it unless the user asks.");

                case 'i':
                    ConsoleUi.Write("instrucciones › ", ConsoleColor.White);
                    var text = interaction.ReadLine();
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(text)) continue; // volver a elegir
                    return new ToolResult(call.Id, call.Name,
                        "The user did NOT apply this change and asks for a revision: " + text +
                        "\nPropose a new version with propose_change.");

                default:
                    return Fail(call, "No choice was made; the change was not applied.");
            }
        }
    }

    private async Task<ToolResult> WriteFileAsync(ToolCall call, string? path, string? content)
    {
        if (string.IsNullOrWhiteSpace(path)) return Fail(call, "Missing 'path'.");
        content ??= "";

        // Rutas sensibles (sistema, ssh, arranque del shell...) o confirmación global: siempre con diff.
        string? fullPath = null;
        try { fullPath = FileTools.Resolve(path); } catch (ArgumentException) { }
        if (confirmAllChanges || (fullPath is not null && FileTools.IsSensitivePath(fullPath)))
            return await ProposeChangeAsync(call, path, content,
                confirmAllChanges ? "confirmación de cambios activada" : "ruta sensible: requiere confirmación");

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
            var full = FileTools.Resolve(path);
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

    private static int? Int(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n)
            ? n
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
            - Inspect before changing. Prefer the built-in tools over shell commands for files: list_dir (tree), read_file, stat_path (exists?), write_file, propose_change. They behave the same on every OS.
            - Writing files: use write_file for ordinary files. Use propose_change (which shows the user a diff to apply, discard or revise) for critical changes, configuration files, system files that could cause problems, or whenever the user asked to confirm changes. Do not use it for trivial edits; stay unobtrusive.
            - If propose_change reports that the user asked for a revision, produce a new version following their instructions. If they discarded it, do not insist.
            - Before overwriting an existing file you did not create, check it with stat_path/read_file.
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

