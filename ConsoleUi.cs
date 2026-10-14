namespace Como;

/// <summary>Salida por consola con colores (solo si es un terminal y no está desactivado).</summary>
public static class ConsoleUi
{
    public static bool NoColor { get; set; }

    private static bool Colors(bool error) =>
        !NoColor
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && !(error ? Console.IsErrorRedirected : Console.IsOutputRedirected);

    public static void Write(string text, ConsoleColor color, bool error = false)
    {
        var writer = error ? Console.Error : Console.Out;
        if (!Colors(error))
        {
            writer.Write(text);
            return;
        }
        Console.ForegroundColor = color;
        writer.Write(text);
        Console.ResetColor();
    }

    public static void WriteLine(string text, ConsoleColor color, bool error = false)
    {
        Write(text, color, error);
        (error ? Console.Error : Console.Out).WriteLine();
    }

    public static void Error(string text) => WriteLine(text, ConsoleColor.Red, error: true);
}
