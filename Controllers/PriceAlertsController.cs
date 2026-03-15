using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Inmobiscrap.Models;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/price-alerts")]
[Authorize]
public class PriceAlertsController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public PriceAlertsController(ApplicationDbContext context) => _context = context;

    private int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : 0;
    }

    // GET /api/price-alerts — IDs de propiedades con alerta activa para el usuario
    [HttpGet]
    public async Task<IActionResult> GetMyAlerts()
    {
        var userId = GetUserId();
        var alertPropertyIds = await _context.PriceAlerts
            .Where(a => a.UserId == userId)
            .Select(a => a.PropertyId)
            .ToListAsync();

        return Ok(alertPropertyIds);
    }

    // POST /api/price-alerts/{propertyId} — Suscribirse (solo Pro)
    [HttpPost("{propertyId:int}")]
    public async Task<IActionResult> Subscribe(int propertyId)
    {
        var userId = GetUserId();

        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Plan, u.Role })
            .FirstOrDefaultAsync();

        if (user == null) return Unauthorized();

        if (user.Plan != "pro" && user.Role != "admin")
            return StatusCode(403, new { message = "Las alertas de precio son exclusivas del Plan Pro." });

        var propertyExists = await _context.Properties.AnyAsync(p => p.Id == propertyId);
        if (!propertyExists) return NotFound(new { message = "Propiedad no encontrada." });

        var already = await _context.PriceAlerts
            .AnyAsync(a => a.UserId == userId && a.PropertyId == propertyId);

        if (already)
            return Ok(new { message = "Ya tenías alerta activa para esta propiedad." });

        _context.PriceAlerts.Add(new PriceAlert
        {
            UserId     = userId,
            PropertyId = propertyId,
            CreatedAt  = DateTime.UtcNow,
        });

        await _context.SaveChangesAsync();
        return Ok(new { message = "Alerta activada. Te avisaremos por email cuando baje el precio." });
    }

    // DELETE /api/price-alerts/{propertyId} — Desuscribirse
    [HttpDelete("{propertyId:int}")]
    public async Task<IActionResult> Unsubscribe(int propertyId)
    {
        var userId = GetUserId();

        var alert = await _context.PriceAlerts
            .FirstOrDefaultAsync(a => a.UserId == userId && a.PropertyId == propertyId);

        if (alert == null) return NotFound(new { message = "No tenías alerta para esta propiedad." });

        _context.PriceAlerts.Remove(alert);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Alerta desactivada." });
    }
}
