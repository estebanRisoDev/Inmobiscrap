namespace Inmobiscrap.Infrastructure;

/// <summary>
/// Lectura de variables de entorno tratando "" como no definida.
/// docker-compose y DotNetEnv crean variables vacías cuando la línea del .env
/// existe sin valor (ej. "MP_TEST_PAYER_EMAIL="); con "??" esas variables
/// vacías ganaban sobre el fallback.
/// </summary>
public static class EnvConfig
{
    public static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static bool GetBool(string name) =>
        string.Equals(Get(name), "true", StringComparison.OrdinalIgnoreCase);
}

public static class DateTimeUtc
{
    /// <summary>
    /// Npgsql solo acepta DateTime con Kind=Utc en columnas "timestamp with time zone".
    /// Fechas parseadas de JSON/query string ("2026-01-15") llegan como Unspecified.
    /// </summary>
    public static DateTime ToUtc(this DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc   => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static DateTime? ToUtc(this DateTime? value) => value?.ToUtc();
}
