using System.Text;

namespace Hazme;

/// <summary>Utilidades de archivos usadas por las herramientas internas (portables entre sistemas).</summary>
internal static class FileTools
{
    private const int MaxListEntries = 500;
    public const int MaxFileChars = 64_000;

    /// <summary>Expande "~" y resuelve rutas relativas contra el directorio actual.</summary>
    public static string Resolve(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Rutas cuya modificación puede dejar el sistema o el acceso del usuario en mal estado.
    /// Los cambios en ellas siempre se confirman con diff, aunque la IA use write_file.
    /// </summary>
    public static bool IsSensitivePath(string fullPath)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dirs = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        }
        else
        {
            dirs.AddRange(["/etc", "/boot", "/usr", "/bin", "/sbin", "/lib", "/lib64", "/var/lib", "/sys", "/proc", "/dev"]);
        }
        if (home.Length > 0)
        {
            dirs.Add(Path.Combine(home, ".ssh"));
            dirs.Add(Path.Combine(home, ".gnupg"));
            dirs.Add(Path.Combine(home, ".como"));
        }

        foreach (var d in dirs.Where(d => d.Length > 0))
        {
            var prefix = d.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, cmp) || string.Equals(fullPath, d, cmp)) return true;
        }

        // Archivos de arranque del shell en la carpeta personal.
        if (home.Length > 0 && string.Equals(Path.GetDirectoryName(fullPath), home.TrimEnd(Path.DirectorySeparatorChar), cmp))
        {
            string[] rc = [".bashrc", ".bash_profile", ".profile", ".zshrc", ".zprofile", ".zshenv", ".bash_login"];
            if (rc.Contains(Path.GetFileName(fullPath), StringComparer.Ordinal)) return true;
        }
        return false;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
    };

    public static bool LooksBinary(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[Math.Min(8000, (int)Math.Min(fs.Length, 8000))];
        var n = fs.Read(buf, 0, buf.Length);
        return buf.AsSpan(0, n).Contains((byte)0);
    }

    /// <summary>Lista en árbol (dirs primero, luego archivos con tamaño). Tope de entradas y de profundidad.</summary>
    public static string ListTree(string root, int maxDepth)
    {
        var sb = new StringBuilder();
        sb.Append(root).Append(Path.DirectorySeparatorChar).Append('\n');
        var count = 0;
        var truncated = false;

        void Walk(string dir, string prefix, int depth)
        {
            List<string> dirs, files;
            try
            {
                dirs = Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase).ToList();
                files = Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                sb.Append(prefix).Append("└── [sin acceso]\n");
                return;
            }

            var all = dirs.Concat(files).ToList();
            for (var i = 0; i < all.Count; i++)
            {
                if (count >= MaxListEntries)
                {
                    truncated = true;
                    return;
                }
                count++;

                var entry = all[i];
                var last = i == all.Count - 1;
                var isDir = i < dirs.Count;
                sb.Append(prefix).Append(last ? "└── " : "├── ").Append(Path.GetFileName(entry));

                if (isDir)
                {
                    sb.Append("/\n");
                    var isLink = new DirectoryInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint);
                    if (depth < maxDepth && !isLink)
                        Walk(entry, prefix + (last ? "    " : "│   "), depth + 1);
                }
                else
                {
                    long size;
                    try { size = new FileInfo(entry).Length; } catch (IOException) { size = -1; }
                    sb.Append(size >= 0 ? $" ({FormatSize(size)})" : "").Append('\n');
                }
            }
        }

        Walk(root, "", 1);
        if (truncated) sb.Append($"[... lista truncada a {MaxListEntries} entradas ...]\n");
        return sb.ToString();
    }
}
