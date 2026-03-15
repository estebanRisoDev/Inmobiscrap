using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Inmobiscrap.Services;

namespace Inmobiscrap.Jobs;

/// <summary>
/// Job diario (Hangfire) que detecta bajadas de precio en las últimas 25h
/// y envía emails a usuarios Pro suscritos a esas propiedades.
/// </summary>
public class PriceAlertJob
{
    private readonly ApplicationDbContext _context;
    private readonly IEmailService _email;
    private readonly ILogger<PriceAlertJob> _logger;

    public PriceAlertJob(
        ApplicationDbContext context,
        IEmailService email,
        ILogger<PriceAlertJob> logger)
    {
        _context = context;
        _email   = email;
        _logger  = logger;
    }

    public async Task ExecuteAsync()
    {
        var since = DateTime.UtcNow.AddHours(-25);

        // Propiedades que bajaron de precio en las últimas 25h
        var droppedIds = await _context.Properties
            .Where(p => p.PriceChangedAt >= since
                     && p.PreviousPrice.HasValue && p.Price.HasValue
                     && p.Price < p.PreviousPrice)
            .Select(p => p.Id)
            .ToListAsync();

        if (droppedIds.Count == 0)
        {
            _logger.LogInformation("PriceAlertJob: sin bajadas de precio en las últimas 25h.");
            return;
        }

        _logger.LogInformation("PriceAlertJob: {Count} propiedad(es) con precio reducido.", droppedIds.Count);

        // Alertas activas de usuarios Pro sobre esas propiedades,
        // que no hayan sido notificadas después del último cambio de precio
        var alerts = await _context.PriceAlerts
            .Include(a => a.User)
            .Include(a => a.Property)
            .Where(a => droppedIds.Contains(a.PropertyId)
                     && (a.User.Plan == "pro" || a.User.Role == "admin")
                     && (a.LastNotifiedAt == null || a.LastNotifiedAt < a.Property.PriceChangedAt))
            .ToListAsync();

        if (alerts.Count == 0)
        {
            _logger.LogInformation("PriceAlertJob: ningún usuario Pro suscrito a las propiedades con bajada.");
            return;
        }

        int sent = 0;

        foreach (var alert in alerts)
        {
            var p = alert.Property;
            var drop = p.PreviousPrice - p.Price;
            var dropPct = p.PreviousPrice > 0
                ? Math.Round((double)(drop! / p.PreviousPrice!) * 100, 1)
                : 0;

            var currency = p.Currency ?? "CLP";
            var formatted = currency == "UF"
                ? $"UF {p.Price:N2}"
                : $"${p.Price:N0} CLP";
            var formattedPrev = currency == "UF"
                ? $"UF {p.PreviousPrice:N2}"
                : $"${p.PreviousPrice:N0} CLP";

            var subject = $"📉 Bajó el precio: {p.Title}";
            var html    = BuildEmailHtml(alert.User.Name, p.Title, p.City, formattedPrev, formatted, dropPct, p.SourceUrl);

            await _email.SendAsync(alert.User.Email, alert.User.Name, subject, html);

            alert.LastNotifiedAt = DateTime.UtcNow;
            sent++;
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("PriceAlertJob: {Sent} notificación(es) enviadas.", sent);
    }

    private static string BuildEmailHtml(
        string userName, string? title, string? city,
        string prevPrice, string newPrice, double dropPct, string? url)
    {
        var linkHtml = !string.IsNullOrWhiteSpace(url)
            ? $"<a href=\"{url}\" style=\"display:inline-block;margin-top:20px;padding:12px 24px;background:#2a9d8f;color:#fff;border-radius:8px;text-decoration:none;font-weight:700;\">Ver propiedad →</a>"
            : "";

        return $@"
<!DOCTYPE html>
<html lang=""es"">
<head><meta charset=""UTF-8"" /><meta name=""viewport"" content=""width=device-width,initial-scale=1"" /></head>
<body style=""margin:0;padding:0;background:#f4f6f8;font-family:'Segoe UI',Arial,sans-serif;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f4f6f8;padding:32px 0;"">
    <tr><td align=""center"">
      <table width=""560"" cellpadding=""0"" cellspacing=""0"" style=""background:#fff;border-radius:12px;overflow:hidden;box-shadow:0 2px 16px rgba(0,0,0,0.08);"">

        <!-- Header -->
        <tr>
          <td style=""background:linear-gradient(135deg,#0f4c81,#2a9d8f);padding:28px 32px;"">
            <h1 style=""margin:0;color:#fff;font-size:22px;font-weight:800;letter-spacing:-0.5px;"">📉 Bajada de precio</h1>
            <p style=""margin:6px 0 0;color:rgba(255,255,255,0.85);font-size:14px;"">InmobiScrap · Alerta de precio</p>
          </td>
        </tr>

        <!-- Body -->
        <tr>
          <td style=""padding:28px 32px;"">
            <p style=""margin:0 0 16px;color:#374151;font-size:15px;"">Hola <strong>{userName}</strong>,</p>
            <p style=""margin:0 0 20px;color:#374151;font-size:15px;"">Una propiedad que sigues acaba de bajar de precio:</p>

            <!-- Property card -->
            <div style=""background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:20px;margin-bottom:20px;"">
              <p style=""margin:0 0 6px;font-size:16px;font-weight:700;color:#0f172a;"">{title ?? "Propiedad"}</p>
              {(city != null ? $"<p style=\"margin:0 0 16px;font-size:13px;color:#64748b;\">{city}</p>" : "")}
              <table cellpadding=""0"" cellspacing=""0"">
                <tr>
                  <td style=""padding-right:24px;"">
                    <p style=""margin:0;font-size:12px;color:#94a3b8;text-transform:uppercase;letter-spacing:0.5px;"">Precio anterior</p>
                    <p style=""margin:4px 0 0;font-size:18px;font-weight:700;color:#64748b;text-decoration:line-through;"">{prevPrice}</p>
                  </td>
                  <td>
                    <p style=""margin:0;font-size:12px;color:#94a3b8;text-transform:uppercase;letter-spacing:0.5px;"">Nuevo precio</p>
                    <p style=""margin:4px 0 0;font-size:22px;font-weight:800;color:#2a9d8f;"">{newPrice}</p>
                  </td>
                </tr>
              </table>
              <div style=""margin-top:12px;display:inline-block;background:#dcfce7;border:1px solid #86efac;border-radius:6px;padding:4px 12px;"">
                <span style=""font-size:13px;font-weight:700;color:#16a34a;"">▼ {dropPct}% de reducción</span>
              </div>
            </div>

            {linkHtml}

            <p style=""margin:24px 0 0;font-size:13px;color:#94a3b8;"">
              Recibiste este email porque activaste una alerta de precio en InmobiScrap.<br/>
              Puedes desactivarla en cualquier momento desde el dashboard.
            </p>
          </td>
        </tr>

        <!-- Footer -->
        <tr>
          <td style=""background:#f8fafc;padding:16px 32px;border-top:1px solid #e2e8f0;"">
            <p style=""margin:0;font-size:12px;color:#94a3b8;text-align:center;"">InmobiScrap · Inteligencia inmobiliaria para Chile</p>
          </td>
        </tr>

      </table>
    </td></tr>
  </table>
</body>
</html>";
    }
}
