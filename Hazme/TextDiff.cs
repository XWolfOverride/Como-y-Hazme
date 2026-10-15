using Como;

namespace Hazme;

/// <summary>Diff de líneas (LCS) y su visualización en formato unificado con colores.</summary>
internal static class TextDiff
{
    public enum Op { Equal, Delete, Insert }

    public readonly record struct DiffLine(Op Op, string Text);

    private const long MaxCells = 9_000_000; // tope de la tabla LCS (memoria ~18 MB)

    public static string[] SplitLines(string text)
    {
        var parts = text.Replace("\r\n", "\n").Split('\n');
        return parts.Length > 0 && parts[^1] == "" ? parts[..^1] : parts;
    }

    /// <summary>Calcula las diferencias; null si los textos son demasiado grandes para comparar con precisión.</summary>
    public static List<DiffLine>? Compute(string[] a, string[] b)
    {
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }

        var n = endA - start;
        var m = endB - start;
        if ((long)(n + 1) * (m + 1) > MaxCells) return null;

        var result = new List<DiffLine>(a.Length + b.Length);
        for (var i = 0; i < start; i++) result.Add(new DiffLine(Op.Equal, a[i]));

        // Tabla LCS sobre la parte central: t[i,j] = LCS de a[start+i..] y b[start+j..]
        var w = m + 1;
        var t = new ushort[(n + 1) * w];
        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            t[i * w + j] = a[start + i] == b[start + j]
                ? (ushort)(t[(i + 1) * w + j + 1] + 1)
                : Math.Max(t[(i + 1) * w + j], t[i * w + j + 1]);

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[start + x] == b[start + y])
            {
                result.Add(new DiffLine(Op.Equal, a[start + x]));
                x++;
                y++;
            }
            else if (t[(x + 1) * w + y] >= t[x * w + y + 1])
            {
                result.Add(new DiffLine(Op.Delete, a[start + x++]));
            }
            else
            {
                result.Add(new DiffLine(Op.Insert, b[start + y++]));
            }
        }
        while (x < n) result.Add(new DiffLine(Op.Delete, a[start + x++]));
        while (y < m) result.Add(new DiffLine(Op.Insert, b[start + y++]));

        for (var i = endA; i < a.Length; i++) result.Add(new DiffLine(Op.Equal, a[i]));
        return result;
    }

    public static (int Added, int Removed) Count(List<DiffLine> diff) =>
        (diff.Count(d => d.Op == Op.Insert), diff.Count(d => d.Op == Op.Delete));

    /// <summary>Pinta el diff unificado (con 3 líneas de contexto). Devuelve false si se truncó por longitud.</summary>
    public static bool Print(List<DiffLine> diff, int maxLines = 400, int context = 3)
    {
        // Numeración de líneas antiguas y nuevas para las cabeceras @@.
        var oldNo = new int[diff.Count];
        var newNo = new int[diff.Count];
        int o = 1, nw = 1;
        for (var i = 0; i < diff.Count; i++)
        {
            oldNo[i] = o;
            newNo[i] = nw;
            if (diff[i].Op != Op.Insert) o++;
            if (diff[i].Op != Op.Delete) nw++;
        }

        var printed = 0;
        var i2 = 0;
        while (i2 < diff.Count)
        {
            // Busca el siguiente cambio.
            while (i2 < diff.Count && diff[i2].Op == Op.Equal) i2++;
            if (i2 >= diff.Count) break;

            var from = Math.Max(0, i2 - context);
            // Extiende el hunk mientras haya cambios dentro de la ventana de contexto.
            var to = i2;
            var lastChange = i2;
            while (to < diff.Count)
            {
                if (diff[to].Op != Op.Equal) lastChange = to;
                else if (to - lastChange > context * 2) break;
                to++;
            }
            to = Math.Min(diff.Count, lastChange + context + 1);

            var oldCount = 0;
            var newCount = 0;
            for (var k = from; k < to; k++)
            {
                if (diff[k].Op != Op.Insert) oldCount++;
                if (diff[k].Op != Op.Delete) newCount++;
            }

            if (printed >= maxLines) return false;
            ConsoleUi.WriteLine($"@@ -{oldNo[from]},{oldCount} +{newNo[from]},{newCount} @@", ConsoleColor.Cyan);
            printed++;

            for (var k = from; k < to; k++)
            {
                if (printed >= maxLines) return false;
                var d = diff[k];
                switch (d.Op)
                {
                    case Op.Insert:
                        ConsoleUi.WriteLine("+" + d.Text, ConsoleColor.Green);
                        break;
                    case Op.Delete:
                        ConsoleUi.WriteLine("-" + d.Text, ConsoleColor.Red);
                        break;
                    default:
                        ConsoleUi.WriteLine(" " + d.Text, ConsoleColor.DarkGray);
                        break;
                }
                printed++;
            }
            i2 = to;
        }
        return true;
    }
}
