using Como.Connectors;

namespace Hazme;

/// <summary>Claves de configuración propias de hazme (registradas en el catálogo común al arrancar).</summary>
internal static class HazmeKeys
{
    public const string GraceSeconds = "grace-seconds";
    public const string MaxSteps = "max-steps";
    public const string Shell = "shell";
    public const string ConfirmChanges = "confirm-changes";
    public const string Plan = "plan";
    public const string LessSafe = "less-safe";
    public const string RiskAck = "risk-ack";
    public const string PersistJobs = "persist-jobs";
    public const string JobRetentionHours = "job-retention-hours";

    public const string LessSafeWarning =
        "⚠ MODO MENOS SEGURO ACTIVADO. hazme leerá archivos privados (claves, credenciales, .env...) sin pedirte\n" +
        "  permiso, y su contenido se enviará al servicio de IA. Hazlo solo con una IA local de confianza o si\n" +
        "  aceptas el riesgo. La responsabilidad es tuya. Para desactivarlo: hazme -set less-safe false";

    public static readonly ConfigKey[] All =
    [
        new(GraceSeconds, "hazme: segundos de gracia antes de ejecutar (0 = sin espera)", Default: "5",
            Validate: v => int.TryParse(v, out var n) && n >= 0 ? null : "requiere un número entero >= 0"),
        new(MaxSteps, "hazme: máximo de pasos (llamadas a la IA) por trabajo", Default: "30",
            Validate: v => int.TryParse(v, out var n) && n > 0 ? null : "requiere un número entero > 0"),
        new(Shell, "hazme: shell a usar (vacío = automática)"),
        new(Plan, "hazme: analizar y pedir aprobación de un plan antes de modificar nada (true/false)", Default: "true",
            Validate: v => v is "true" or "false" ? null : "admite 'true' o 'false'"),
        new(LessSafe, "hazme: MODO MENOS SEGURO, la IA lee archivos privados sin pedir permiso (true/false)", Default: "false",
            Validate: v => v is "true" or "false" ? null : "admite 'true' o 'false'",
            Warn: v => v == "true" ? LessSafeWarning : null),
        new(RiskAck, "hazme: has leído y aceptado los riesgos de usar hazme (true/false)", Default: "false",
            Validate: v => v is "true" or "false" ? null : "admite 'true' o 'false'"),
        new(PersistJobs, "hazme: guardar un diario cifrado del trabajo para poder retomarlo si se interrumpe (true/false)", Default: "true",
            Validate: v => v is "true" or "false" ? null : "admite 'true' o 'false'"),
        new(JobRetentionHours, "hazme: horas que se conserva un trabajo interrumpido antes de borrarse", Default: "4",
            Validate: v => int.TryParse(v, out var n) && n > 0 ? null : "requiere un número entero > 0"),
        new(ConfirmChanges, "hazme: confirmar con diff toda escritura de archivos (true/false)", Default: "false",
            Validate: v => v is "true" or "false" ? null : "admite 'true' o 'false'"),
    ];
}
