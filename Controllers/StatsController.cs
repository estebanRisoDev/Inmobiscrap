using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public StatsController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: api/stats
    [HttpGet]
    public async Task<IActionResult> GetStats()
    {
        // ── Properties ─────────────────────────────────────────────────────────
        var totalProperties = await _context.Properties.CountAsync();

        var propertiesByType = await _context.Properties
            .Where(p => p.PropertyType != null)
            .GroupBy(p => p.PropertyType!)
            .Select(g => new { type = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToListAsync();

        var propertiesByCity = await _context.Properties
            .Where(p => p.City != null)
            .GroupBy(p => p.City!)
            .Select(g => new { city = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(10)
            .ToListAsync();

        var propertiesByCurrency = await _context.Properties
            .Where(p => p.Currency != null)
            .GroupBy(p => p.Currency!)
            .Select(g => new { currency = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToListAsync();

        // ── Bots ───────────────────────────────────────────────────────────────
        var bots = await _context.Bots.ToListAsync();

        var totalBots    = bots.Count;
        var activeBots   = bots.Count(b => b.IsActive);
        var inactiveBots = bots.Count(b => !b.IsActive);
        var runningBots  = bots.Count(b => b.Status == "running");

        var botStatusDistribution = bots
            .GroupBy(b => b.Status ?? "idle")
            .Select(g => new { status = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();

        var botsBySource = bots
            .GroupBy(b => b.Source ?? "Otro")
            .Select(g => new { source = g.Key, totalScraped = g.Sum(b => b.TotalScraped) })
            .OrderByDescending(x => x.totalScraped)
            .ToList();

        var recentBots = bots
            .OrderByDescending(b => b.LastRun ?? b.CreatedAt)
            .Take(5)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Source,
                b.Status,
                b.TotalScraped,
                b.LastRun,
                b.IsActive,
            })
            .ToList();

        return Ok(new
        {
            totalProperties,
            totalBots,
            activeBots,
            inactiveBots,
            runningBots,
            propertiesByType,
            propertiesByCity,
            propertiesByCurrency,
            botsBySource,
            botStatusDistribution,
            recentBots,
        });
    }
}
