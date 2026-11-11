using Cronos;

namespace Inmobiscrap.Infrastructure;

/// <summary>
/// Valida expresiones CRON con el mismo formato que acepta Hangfire
/// (5 campos estándar o 6 campos con segundos).
/// </summary>
public static class CronValidator
{
    public static bool IsValid(string? expression, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "La expresión CRON está vacía.";
            return false;
        }

        var parts  = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var format = parts.Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;

        try
        {
            CronExpression.Parse(expression.Trim(), format);
            return true;
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
