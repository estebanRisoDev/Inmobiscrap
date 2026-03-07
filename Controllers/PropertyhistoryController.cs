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
            totalSnapshots           = snapshots.Count,
            snapshotsWithChanges     = snapshots.Count(s => s.HasChanges),
            firstSeen                = snapshots.FirstOrDefault()?.ScrapedAt,
            lastSeen                 = snapshots.LastOrDefault()?.ScrapedAt,
            snapshots,
        });
    }

    /// <summary>
    /// GET /api/properties/price-changes?days=30
    /// Propiedades cuyo precio cambió recientemente.
    /// </summary>
    [HttpGet("price-changes")]
    public async Task<IActionResult> GetPriceChanges(
        [FromQuery] int days  = 30,
        [FromQuery] int limit = 50)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        var changes = await _context.Properties
            .Where(p => p.PriceChangedAt != null && p.PriceChangedAt >= since
                     && p.PreviousPrice  != null && p.Price != null)
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
                currentPrice  = p.Price,
                previousPrice = p.PreviousPrice,
                priceChange   = p.Price - p.PreviousPrice,
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
            period         = $"Últimos {days} días",
            total          = changes.Count,
            priceIncreases = changes.Count(c => c.priceChange > 0),
            priceDecreases = changes.Count(c => c.priceChange < 0),
            items          = changes,
        });
    }

    /// <summary>
    /// GET /api/properties/tracking-stats
    /// Estadísticas generales del sistema de tracking.
    ///
    /// FIX: Retorna los campos que el dashboard frontend espera:
    ///   - newProperties, priceChanges, delisted, unchanged
    ///   - priceHistory: historial mensual desde PropertySnapshots (para el gráfico)
    /// </summary>
    [HttpGet("tracking-stats")]
    public async Task<IActionResult> GetTrackingStats()
    {
        var totalProperties = await _context.Properties.CountAsync();
        var totalSnapshots  = await _context.PropertySnapshots.LongCountAsync();
        var tracked         = await _context.Properties.CountAsync(p => p.TimesScraped > 1);
        var withPriceChange = await _context.Properties.CountAsync(p => p.PreviousPrice != null);

        var recentSnapshots = await _context.PropertySnapshots
            .Where(s => s.ScrapedAt >= DateTime.UtcNow.AddDays(-7))
            .LongCountAsync();

        var delisted = await _context.Properties
            .CountAsync(p => p.LastSeenAt != null
                          && p.LastSeenAt < DateTime.UtcNow.AddDays(-7)
                          && p.TimesScraped > 1);

        // ── Propiedades nuevas: primera vez vistas en los últimos 7 días ─────
        var newProperties = await _context.Properties
            .CountAsync(p => p.FirstSeenAt != null
                          && p.FirstSeenAt >= DateTime.UtcNow.AddDays(-7));

        // ── Sin cambios: vistas más de una vez pero sin ningún cambio ────────
        var unchanged = await _context.PropertySnapshots
            .Where(s => !s.HasChanges)
            .Select(s => s.PropertyId)
            .Distinct()
            .CountAsync();

        // ── Historial mensual de precios separado por moneda ─────────────────
        // Agrupa por (año, mes, moneda) para nunca mezclar UF con CLP en los
        // cálculos de min/avg/max. Antes se agrupaba sin moneda, lo que causaba
        // que valores UF (ej: 3200) aparecieran como minPrice en meses CLP.
        var rawPriceHistory = await _context.PropertySnapshots
            .Where(s => s.Price.HasValue && s.Price > 0 && s.Currency != null)
            .GroupBy(s => new { s.ScrapedAt.Year, s.ScrapedAt.Month, s.Currency })
            .Select(g => new
            {
                Year     = g.Key.Year,
                Month    = g.Key.Month,
                Currency = g.Key.Currency,
                AvgPrice = g.Average(s => (double)s.Price!),
                MinPrice = g.Min(s => (double)s.Price!),
                MaxPrice = g.Max(s => (double)s.Price!),
                Count    = g.Count(),
            })
            .OrderBy(g => g.Year)
            .ThenBy(g => g.Month)
            .ToListAsync();

        var priceHistoryCLP = rawPriceHistory
            .Where(g => g.Currency == "CLP")
            .Select(g => new
            {
                mes      = $"{g.Year}-{g.Month:D2}",
                avgPrice = Math.Round(g.AvgPrice, 2),
                minPrice = Math.Round(g.MinPrice, 2),
                maxPrice = Math.Round(g.MaxPrice, 2),
                count    = g.Count,
            }).ToList();

        var priceHistoryUF = rawPriceHistory
            .Where(g => g.Currency == "UF")
            .Select(g => new
            {
                mes      = $"{g.Year}-{g.Month:D2}",
                avgPrice = Math.Round(g.AvgPrice, 2),
                minPrice = Math.Round(g.MinPrice, 2),
                maxPrice = Math.Round(g.MaxPrice, 2),
                count    = g.Count,
            }).ToList();

        // priceHistory: fallback genérico (la moneda con más datos)
        var priceHistory = priceHistoryCLP.Count >= priceHistoryUF.Count
            ? priceHistoryCLP.Cast<object>().ToList()
            : priceHistoryUF.Cast<object>().ToList();

        return Ok(new
        {
            // ── Campos que el frontend usa en los KPI cards ────────────────
            newProperties,                        // "Nuevas (última ejecución)"
            priceChanges     = withPriceChange,   // "Cambios de precio detectados"
            delisted,                             // "Despublicadas / eliminadas"
            unchanged,                            // "Sin cambios"

            // ── Historial mensual separado por moneda ──────────────────────
            priceHistoryCLP,   // page.tsx línea 441: trackingStats?.priceHistoryCLP
            priceHistoryUF,    // page.tsx línea 442: trackingStats?.priceHistoryUF
            priceHistory,      // fallback genérico (la moneda con más datos)

            // ── Stats adicionales (para otros usos) ───────────────────────
            totalProperties,
            totalSnapshots,
            propertiesTracked          = tracked,
            propertiesWithPriceChanges = withPriceChange,
            snapshotsLast7Days         = recentSnapshots,
            possiblyDelisted           = delisted,
            avgSnapshotsPerProperty    = totalProperties > 0
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
        [FromQuery] int days  = 7,
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