using Como.Connectors;

namespace Hazme;

/// <summary>Claves de configuración propias de hazme (registradas en el catálogo común al arrancar).</summary>
internal static class HazmeKeys
{
    public const string GraceSeconds = "grace-seconds";
    public const string MaxSteps = "max-steps";
    public const string Shell = "shell";

    public static readonly ConfigKey[] All =
    [
        new(GraceSeconds, "hazme: segundos de gracia antes de ejecutar (0 = sin espera)", Default: "3",
            Validate: v => int.TryParse(v, out var n) && n >= 0 ? null : "requiere un número entero >= 0"),
        new(MaxSteps, "hazme: máximo de pasos (llamadas a la IA) por trabajo", Default: "30",
            Validate: v => int.TryParse(v, out var n) && n > 0 ? null : "requiere un número entero > 0"),
        new(Shell, "hazme: shell a usar (vacío = automática)"),
    ];
}
