using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Microsoft.AspNetCore.Authorization;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/properties")]
[Authorize]
public class PropertyHistoryController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public PropertyHistoryController(ApplicationDbContext context) => _context = context;

    /// <summary>
    /// GET /api/properties/{id}/timeline
    /// Línea de tiempo completa: todos los snapshots de una propiedad.
    /// Cada snapshot es una observación con los valores de ese momento.
    /// </summary>
    [HttpGet("{id}/timeline")]
    public async Task<IActionResult> GetTimeline(int id)
    {
        var property = await _context.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (property == null) return NotFound();

        var snapshots = await _context.PropertySnapshots
            .Where(s => s.PropertyId == id)
            .OrderBy(s => s.ScrapedAt)
            .Select(s => new
            {
                s.Id,
                s.ScrapedAt,
                s.BotId,
                s.Price,
                s.Currency,
                s.Bedrooms,
                s.Bathrooms,
                s.Area,
                s.PropertyType,
                s.Title,
                s.HasChanges,
                s.ChangedFields,
            })
            .ToListAsync();

        return Ok(new
        {
            property = new
            {
                property.Id,
                property.Title,
                property.SourceUrl,
                property.Price,
                property.PreviousPrice,
                property.Currency,
                property.City,
                property.Region,
                property.Neighborhood,
                property.Fingerprint,
                property.FirstSeenAt,
                property.LastSeenAt,
                property.TimesScraped,
                property.ListingStatus,
                property.PriceChangedAt,
            },
            totalSnapshots = snapshots.Count,
            snapshotsWithChanges = snapshots.Count(s => s.HasChanges),
            firstSeen = snapshots.FirstOrDefault()?.ScrapedAt,
            lastSeen = snapshots.LastOrDefault()?.ScrapedAt,
            snapshots,
        });
    }

    /// <summary>
    /// GET /api/properties/price-changes?days=30
    /// Propiedades cuyo precio cambió recientemente.
    /// </summary>
    [HttpGet("price-changes")]
    public async Task<IActionResult> GetPriceChanges(
        [FromQuery] int days = 30,
        [FromQuery] int limit = 50)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        var changes = await _context.Properties
            .Where(p => p.PriceChangedAt != null && p.PriceChangedAt >= since
                     && p.PreviousPrice != null && p.Price != null)
            .OrderByDescending(p => p.PriceChangedAt)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.City,
                p.Neighborhood,
                p.PropertyType,
                p.Currency,
                currentPrice = p.Price,
                previousPrice = p.PreviousPrice,
                priceChange = p.Price - p.PreviousPrice,
                changePercent = p.PreviousPrice > 0
                    ? Math.Round((double)((p.Price - p.PreviousPrice) / p.PreviousPrice * 100), 1)
                    : 0,
                p.PriceChangedAt,
                p.SourceUrl,
                p.TimesScraped,
            })
            .ToListAsync();

        return Ok(new
        {
            period = $"Últimos {days} días",
            total = changes.Count,
            priceIncreases = changes.Count(c => c.priceChange > 0),
            priceDecreases = changes.Count(c => c.priceChange < 0),
            items = changes,
        });
    }

    /// <summary>
    /// GET /api/properties/tracking-stats
    /// Estadísticas generales del sistema de tracking.
    /// </summary>
    [HttpGet("tracking-stats")]
    public async Task<IActionResult> GetTrackingStats()
    {
        var totalProperties = await _context.Properties.CountAsync();
        var totalSnapshots  = await _context.PropertySnapshots.LongCountAsync();
        var tracked = await _context.Properties.CountAsync(p => p.TimesScraped > 1);
        var withPriceChange = await _context.Properties.CountAsync(p => p.PreviousPrice != null);

        var recentSnapshots = await _context.PropertySnapshots
            .Where(s => s.ScrapedAt >= DateTime.UtcNow.AddDays(-7))
            .LongCountAsync();

        var delisted = await _context.Properties
            .CountAsync(p => p.LastSeenAt != null
                          && p.LastSeenAt < DateTime.UtcNow.AddDays(-7)
                          && p.TimesScraped > 1);

        return Ok(new
        {
            totalProperties,
            totalSnapshots,
            propertiesTracked = tracked,
            propertiesWithPriceChanges = withPriceChange,
            snapshotsLast7Days = recentSnapshots,
            possiblyDelisted = delisted,
            avgSnapshotsPerProperty = totalProperties > 0
                ? Math.Round((double)totalSnapshots / totalProperties, 1)
                : 0,
        });
    }

    /// <summary>
    /// GET /api/properties/delisted?days=7
    /// Propiedades que dejaron de aparecer en los scrapes.
    /// </summary>
    [HttpGet("delisted")]
    public async Task<IActionResult> GetDelisted(
        [FromQuery] int days = 7,
        [FromQuery] int limit = 50)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);

        var delisted = await _context.Properties
            .Where(p => p.LastSeenAt != null && p.LastSeenAt < cutoff && p.TimesScraped > 1)
            .OrderByDescending(p => p.LastSeenAt)
            .Take(limit)
            .Select(p => new
            {
                p.Id, p.Title, p.City, p.Neighborhood, p.PropertyType,
                p.Price, p.Currency, p.LastSeenAt, p.FirstSeenAt, p.TimesScraped,
                daysSinceLastSeen = (DateTime.UtcNow - p.LastSeenAt!.Value).Days,
                p.SourceUrl,
            })
            .ToListAsync();

        return Ok(new { cutoffDays = days, total = delisted.Count, items = delisted });
    }
}