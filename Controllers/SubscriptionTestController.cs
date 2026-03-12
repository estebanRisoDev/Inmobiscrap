using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Inmobiscrap.Jobs;
using Inmobiscrap.Models;

namespace Inmobiscrap.Controllers;

public record SetNextBillingRequest(DateTime Date);

[ApiController]
[Route("api/admin/subscriptions")]
[Authorize(Roles = "admin")]
public class SubscriptionTestController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<SubscriptionTestController> _logger;

    public SubscriptionTestController(
        ApplicationDbContext context,
        IBackgroundJobClient backgroundJobs,
        ILogger<SubscriptionTestController> logger)
    {
        _context        = context;
        _backgroundJobs = backgroundJobs;
        _logger         = logger;
    }

    // ── GET /api/admin/subscriptions/pro-users ────────────────────────────────
    [HttpGet("pro-users")]
    public async Task<IActionResult> GetProUsers()
    {
        var users = await _context.Users
            .Where(u => u.Plan == "pro")
            .OrderBy(u => u.Id)
            .Select(u => new
            {
                u.Id,
                u.Name,
                u.Email,
                u.Plan,
                u.Credits,
                u.CreditsBeforePro,
                u.MpSubscriptionId,
                u.NextBillingDate,
                u.CreatedAt,
            })
            .ToListAsync();

        return Ok(users);
    }

    // ── GET /api/admin/subscriptions/all-users ────────────────────────────────
    [HttpGet("all-users")]
    public async Task<IActionResult> GetAllUsers()
    {
        var users = await _context.Users
            .OrderBy(u => u.Id)
            .Select(u => new
            {
                u.Id,
                u.Name,
                u.Email,
                u.Plan,
                u.Credits,
                u.CreditsBeforePro,
                u.MpSubscriptionId,
                u.NextBillingDate,
                u.Role,
            })
            .ToListAsync();

        return Ok(users);
    }

    // ── POST /api/admin/subscriptions/activate-pro/{userId} ──────────────────
    /// Manually activates Pro (bypasses MP). Useful for testing.
    [HttpPost("activate-pro/{userId:int}")]
    public async Task<IActionResult> ActivatePro(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "Usuario no encontrado." });

        if (user.Plan == "pro")
            return BadRequest(new { message = "El usuario ya es Pro." });

        user.CreditsBeforePro = user.Credits;
        user.Plan             = "pro";
        user.Credits          = 0;
        user.MpSubscriptionId = $"test-{userId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        user.NextBillingDate  = DateTime.UtcNow.AddMonths(1);

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "ADMIN activate-pro: userId={UserId} activado (guardó {Prev} créditos). nextBilling={Date}",
            userId, user.CreditsBeforePro, user.NextBillingDate);

        return Ok(new
        {
            message          = $"Usuario {userId} activado como Pro hasta {user.NextBillingDate:yyyy-MM-dd HH:mm:ss} UTC.",
            userId,
            plan             = "pro",
            nextBillingDate  = user.NextBillingDate,
            creditsBeforePro = user.CreditsBeforePro,
        });
    }

    // ── POST /api/admin/subscriptions/force-expire/{userId} ──────────────────
    /// Simulates subscription expiry: downgrades user to base immediately.
    [HttpPost("force-expire/{userId:int}")]
    public async Task<IActionResult> ForceExpire(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "Usuario no encontrado." });

        if (user.Plan != "pro")
            return BadRequest(new { message = "El usuario no tiene plan Pro." });

        var restored = user.CreditsBeforePro ?? 50;
        user.Plan             = "base";
        user.Credits          = restored;
        user.CreditsBeforePro = null;
        user.MpSubscriptionId = null;
        user.NextBillingDate  = null;

        await _context.SaveChangesAsync();

        _logger.LogWarning(
            "ADMIN force-expire: userId={UserId} degradado a base ({Credits} créditos).",
            userId, restored);

        return Ok(new
        {
            message = $"Usuario {userId} degradado a base con {restored} créditos.",
            userId,
            credits = restored,
        });
    }

    // ── POST /api/admin/subscriptions/simulate-renewal/{userId} ──────────────
    /// Simulates a successful monthly renewal going through the same logic as the
    /// real preapproval webhook: reactivates Pro and sets NextBillingDate = now + 1 month.
    /// This is equivalent to MP sending us a subscription_preapproval webhook with status=authorized.
    [HttpPost("simulate-renewal/{userId:int}")]
    public async Task<IActionResult> SimulateRenewal(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "Usuario no encontrado." });

        if (user.Plan != "pro")
            return BadRequest(new { message = "El usuario no tiene plan Pro activo." });

        var oldDate = user.NextBillingDate ?? DateTime.UtcNow;
        var newDate = DateTime.UtcNow.AddMonths(1);

        // Simulate what ActivateProSubscription does when a renewal webhook arrives:
        // plan stays "pro", keep MpSubscriptionId, update NextBillingDate.
        user.NextBillingDate = newDate;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "ADMIN simulate-renewal (webhook path): userId={UserId} {Old} → {New}",
            userId, oldDate, newDate);

        return Ok(new
        {
            message         = $"Renovación simulada (como si MP cobró). NextBillingDate: {newDate:yyyy-MM-dd HH:mm:ss} UTC",
            userId,
            oldBillingDate  = oldDate,
            nextBillingDate = newDate,
        });
    }

    // ── POST /api/admin/subscriptions/run-expiry-inline/{userId} ─────────────
    /// Runs the expiry check inline (synchronous, not queued) for a specific user.
    /// Useful to instantly verify the expiry logic without waiting for Hangfire.
    [HttpPost("run-expiry-inline/{userId:int}")]
    public async Task<IActionResult> RunExpiryInline(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "Usuario no encontrado." });

        if (user.Plan != "pro")
            return Ok(new { message = "El usuario ya está en Base, no hay nada que expirar.", degraded = false });

        if (user.NextBillingDate == null || user.NextBillingDate > DateTime.UtcNow)
            return Ok(new
            {
                message  = $"NextBillingDate aún no venció ({user.NextBillingDate:yyyy-MM-dd HH:mm:ss} UTC). Usa '10s' o 'Vencido ya' primero.",
                degraded = false,
                nextBillingDate = user.NextBillingDate,
            });

        // Same logic as SubscriptionExpiryJob
        var restored = user.CreditsBeforePro ?? 50;
        user.Plan             = "base";
        user.Credits          = restored;
        user.CreditsBeforePro = null;
        user.MpSubscriptionId = null;
        user.NextBillingDate  = null;

        await _context.SaveChangesAsync();

        _logger.LogWarning(
            "ADMIN run-expiry-inline: userId={UserId} degradado a base ({Credits} créditos).",
            userId, restored);

        return Ok(new
        {
            message  = $"Usuario {userId} degradado a base con {restored} créditos (inline, sin Hangfire).",
            degraded = true,
            credits  = restored,
        });
    }

    // ── POST /api/admin/subscriptions/set-next-billing/{userId} ──────────────
    /// Sets NextBillingDate to a specific date/time (e.g. 5 seconds in the future to test expiry).
    [HttpPost("set-next-billing/{userId:int}")]
    public async Task<IActionResult> SetNextBilling(int userId, [FromBody] SetNextBillingRequest req)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "Usuario no encontrado." });

        user.NextBillingDate = req.Date.ToUniversalTime();
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "ADMIN set-next-billing: userId={UserId} nextBilling={Date}",
            userId, user.NextBillingDate);

        return Ok(new
        {
            message         = $"NextBillingDate actualizado a {user.NextBillingDate:yyyy-MM-dd HH:mm:ss} UTC",
            userId,
            nextBillingDate = user.NextBillingDate,
        });
    }

    // ── POST /api/admin/subscriptions/run-expiry-job ──────────────────────────
    /// Enqueues the subscription expiry Hangfire job immediately.
    [HttpPost("run-expiry-job")]
    public IActionResult RunExpiryJob()
    {
        var jobId = _backgroundJobs.Enqueue<SubscriptionExpiryJob>(
            job => job.CheckAndExpireSubscriptionsAsync());

        _logger.LogInformation("ADMIN: enqueued SubscriptionExpiryJob → hangfireJobId={JobId}", jobId);

        return Ok(new { message = "Job encolado en Hangfire.", hangfireJobId = jobId });
    }
}
