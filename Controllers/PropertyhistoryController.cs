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
                s.Region,
                s.City,
                s.Neighborhood,
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

        var newProperties = await _context.Properties
            .CountAsync(p => p.FirstSeenAt != null
                          && p.FirstSeenAt >= DateTime.UtcNow.AddDays(-7));

        // "Sin cambios" = propiedades observadas más de una vez que NUNCA
        // tuvieron ningún cambio detectado en ningún snapshot.
        // La query anterior contaba propiedades con al menos un snapshot
        // sin cambios — pero TODA propiedad tiene su primer snapshot con
        // HasChanges=false, así que siempre daba el total.
        var propertyIdsWithChanges = _context.PropertySnapshots
            .Where(s => s.HasChanges)
            .Select(s => s.PropertyId)
            .Distinct();

        var unchanged = await _context.Properties
            .CountAsync(p => p.TimesScraped > 1
                          && !propertyIdsWithChanges.Contains(p.Id));

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

        var priceHistory = priceHistoryCLP.Count >= priceHistoryUF.Count
            ? priceHistoryCLP.Cast<object>().ToList()
            : priceHistoryUF.Cast<object>().ToList();

        return Ok(new
        {
            newProperties,
            priceChanges     = withPriceChange,
            delisted,
            unchanged,
            priceHistoryCLP,
            priceHistoryUF,
            priceHistory,
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
    /// GET /api/properties/price-history?range=day|week|month
    ///     &amp;currency=UF|CLP
    ///     &amp;region=...&amp;city=...&amp;neighborhood=...&amp;propertyType=...
    ///
    /// FILTROS: Usa los campos desnormalizados del snapshot (Region, City,
    /// Neighborhood, PropertyType). Para snapshots antiguos pre-migración
    /// que no tienen esos campos, fallback a Property vía navegación.
    ///
    /// DEDUPLICACIÓN: Agrupa por (PropertyId, bucket temporal) y toma solo
    /// el último snapshot por propiedad por bucket. Así una propiedad
    /// scrapada N veces en la misma hora/día cuenta UNA sola vez y el
    /// promedio refleja el precio real de cada propiedad, no el volumen
    /// del scrape.
    ///
    ///   - day   → agrupado por hora  (últimas 24h)
    ///   - week  → agrupado por día   (últimos 7 días)
    ///   - month → agrupado por día   (últimos 30 días)
    /// </summary>
    [HttpGet("price-history")]
    public async Task<IActionResult> GetPriceHistory(
        [FromQuery] string  range        = "month",
        [FromQuery] string? currency     = null,
        [FromQuery] string? region       = null,
        [FromQuery] string? city         = null,
        [FromQuery] string? neighborhood = null,
        [FromQuery] string? propertyType = null)
    {
        var now = DateTime.UtcNow;

        DateTime since = range switch
        {
            "day"  => now.AddHours(-24),
            "week" => now.AddDays(-7),
            _      => now.AddDays(-30),
        };

        // ── Query base ───────────────────────────────────────────────────────
        var query = _context.PropertySnapshots
            .Where(s => s.Price.HasValue && s.Price > 0
                     && s.ScrapedAt >= since);

        // ── Filtros geográficos y de tipo ────────────────────────────────────
        // Usa campos del snapshot; fallback a Property para datos antiguos.
        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(s =>
                (s.Region != null && s.Region == region) ||
                (s.Region == null && s.Property.Region == region));

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(s =>
                (s.City != null && s.City == city) ||
                (s.City == null && s.Property.City == city));

        if (!string.IsNullOrWhiteSpace(neighborhood))
            query = query.Where(s =>
                (s.Neighborhood != null && s.Neighborhood == neighborhood) ||
                (s.Neighborhood == null && s.Property.Neighborhood == neighborhood));

        if (!string.IsNullOrWhiteSpace(propertyType))
            query = query.Where(s =>
                (s.PropertyType != null && s.PropertyType == propertyType) ||
                (s.PropertyType == null && s.Property.PropertyType == propertyType));

        // ── Filtro de moneda ─────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(currency) && (currency == "UF" || currency == "CLP"))
            query = query.Where(s => s.Currency == currency);

        // ── Materializar ─────────────────────────────────────────────────────
        var raw = await query
            .Select(s => new
            {
                s.PropertyId,
                s.ScrapedAt,
                Price    = (double)s.Price!,
                Currency = s.Currency ?? "CLP",
            })
            .ToListAsync();

        if (raw.Count == 0)
            return Ok(Array.Empty<object>());

        // ── Auto-detectar moneda dominante ────────────────────────────────────
        if (string.IsNullOrWhiteSpace(currency) || (currency != "UF" && currency != "CLP"))
        {
            var ufCount  = raw.Count(r => r.Currency == "UF");
            var clpCount = raw.Count(r => r.Currency == "CLP");
            var dominant = ufCount >= clpCount ? "UF" : "CLP";
            raw = raw.Where(r => r.Currency == dominant).ToList();
        }

        // ══════════════════════════════════════════════════════════════════════
        // DEDUPLICACIÓN
        //
        // Problema: si un bot scrapea 48 propiedades a las 20:15, el endpoint
        // sin deduplicar devuelve avgPrice = promedio de esas 48 propiedades
        // para la hora 20:00. Eso no es "evolución del precio" sino
        // "distribución de precios del scrape".
        //
        // Solución: por cada (PropertyId, bucket), quedarse con el último
        // snapshot. Así cada propiedad pesa 1 vez por bucket temporal.
        // ══════════════════════════════════════════════════════════════════════

        IEnumerable<object> result;

        if (range == "day")
        {
            var deduped = raw
                .GroupBy(r => new { r.PropertyId, r.ScrapedAt.Year, r.ScrapedAt.Month, r.ScrapedAt.Day, r.ScrapedAt.Hour })
                .Select(g => g.OrderByDescending(r => r.ScrapedAt).First())
                .ToList();

            result = deduped
                .GroupBy(r => new DateTime(r.ScrapedAt.Year, r.ScrapedAt.Month, r.ScrapedAt.Day,
                                           r.ScrapedAt.Hour, 0, 0, DateTimeKind.Utc))
                .OrderBy(g => g.Key)
                .Select(g => (object)new
                {
                    year      = g.Key.Year,
                    month     = g.Key.Month,
                    day       = g.Key.Day,
                    hour      = (int?)g.Key.Hour,
                    dayOfWeek = (int)g.Key.DayOfWeek,
                    avgPrice  = Math.Round(g.Average(r => r.Price), 2),
                    minPrice  = Math.Round(g.Min(r => r.Price), 2),
                    maxPrice  = Math.Round(g.Max(r => r.Price), 2),
                    count     = g.Count(),
                });
        }
        else
        {
            var deduped = raw
                .GroupBy(r => new { r.PropertyId, r.ScrapedAt.Year, r.ScrapedAt.Month, r.ScrapedAt.Day })
                .Select(g => g.OrderByDescending(r => r.ScrapedAt).First())
                .ToList();

            result = deduped
                .GroupBy(r => r.ScrapedAt.Date)
                .OrderBy(g => g.Key)
                .Select(g => (object)new
                {
                    year      = g.Key.Year,
                    month     = g.Key.Month,
                    day       = g.Key.Day,
                    hour      = (int?)null,
                    dayOfWeek = (int)g.Key.DayOfWeek,
                    avgPrice  = Math.Round(g.Average(r => r.Price), 2),
                    minPrice  = Math.Round(g.Min(r => r.Price), 2),
                    maxPrice  = Math.Round(g.Max(r => r.Price), 2),
                    count     = g.Count(),
                });
        }

        return Ok(result.ToList());
    }

    /// <summary>
    /// GET /api/properties/delisted?days=7
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