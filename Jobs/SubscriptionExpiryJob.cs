using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Inmobiscrap.Data;

namespace Inmobiscrap.Jobs;

public class SubscriptionExpiryJob
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<SubscriptionExpiryJob> _logger;

    public SubscriptionExpiryJob(ApplicationDbContext context, ILogger<SubscriptionExpiryJob> logger)
    {
        _context = context;
        _logger  = logger;
    }

    /// <summary>
    /// Verifica usuarios Pro cuya NextBillingDate ya pasó y los degrada a base.
    /// Corre diariamente via Hangfire. También se puede disparar manualmente desde /api/admin/test.
    /// </summary>
    public async Task CheckAndExpireSubscriptionsAsync()
    {
        var now = DateTime.UtcNow;

        // Degrade Pro users whose paid period has ended.
        // This covers two cases:
        //   1. Cancelled subscriptions (MpSubscriptionId == null) that reached their expiry date.
        //   2. Active subscriptions where MP failed to renew and the date passed.
        var overdueUsers = await _context.Users
            .Where(u => u.Plan == "pro"
                     && u.NextBillingDate != null
                     && u.NextBillingDate < now)
            .ToListAsync();

        if (!overdueUsers.Any())
        {
            _logger.LogInformation("SubscriptionExpiry: no hay suscripciones vencidas.");
            return;
        }

        _logger.LogInformation("SubscriptionExpiry: {Count} suscripción(es) vencida(s).", overdueUsers.Count);

        foreach (var user in overdueUsers)
        {
            var restored = user.CreditsBeforePro ?? 50;
            user.Plan             = "base";
            user.Credits          = restored;
            user.CreditsBeforePro = null;
            user.MpSubscriptionId = null;
            user.NextBillingDate  = null;

            _logger.LogWarning(
                "SubscriptionExpiry: usuario {UserId} ({Email}) degradado a base ({Credits} créditos). Última billing: {Date}",
                user.Id, user.Email, restored, user.NextBillingDate);
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("SubscriptionExpiry: {Count} usuario(s) degradado(s).", overdueUsers.Count);
    }
}
