using System.Text.Json;
using Como.Settings;

namespace Hazme.Peer;

/// <summary>Mensajes JSON de una línea de protocolo; el campo "t" es el tipo.</summary>
internal static class PeerMsg
{
    public static byte[] Build(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int Int(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    public static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Qué es el sistema del otro lado, tal como lo describe su agente.</summary>
internal sealed record PeerInfo(string Machine, string Os, string Shell, string User, string Home, string Cwd, bool IsRoot)
{
    public static PeerInfo ForThisMachine() => new(
        Environment.MachineName,
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        OperatingSystem.IsWindows()
            ? "PowerShell"
            : Path.GetFileName(Environment.GetEnvironmentVariable("SHELL") ?? "sh"),
        Environment.UserName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.CurrentDirectory,
        !OperatingSystem.IsWindows() && Environment.UserName == "root");

    public byte[] ToMessage() => PeerMsg.Build(w =>
    {
        w.WriteString("t", "info");
        w.WriteString("machine", Machine);
        w.WriteString("os", Os);
        w.WriteString("shell", Shell);
        w.WriteString("user", User);
        w.WriteString("home", Home);
        w.WriteString("cwd", Cwd);
        w.WriteBoolean("root", IsRoot);
    });

    public static PeerInfo FromMessage(JsonElement e) => new(
        PeerMsg.Str(e, "machine") ?? "?", PeerMsg.Str(e, "os") ?? "?", PeerMsg.Str(e, "shell") ?? "?",
        PeerMsg.Str(e, "user") ?? "?", PeerMsg.Str(e, "home") ?? "?", PeerMsg.Str(e, "cwd") ?? "?",
        PeerMsg.Bool(e, "root"));
}

/// <summary>El agente no usa la configuración del equipo: no tiene claves ni conectores.</summary>
internal sealed class NoSettings : ISettings
{
    public static NoSettings Instance { get; } = new();
    public string? Get(string key) => null;
}
