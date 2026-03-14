using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Inmobiscrap.Services;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/properties/sold")]
[Authorize(Policy = "ProOrAdmin")]
public class SoldController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IPropertyVerificationService _verificationService;
    private readonly IVerificationJobStatus _jobStatus;

    public SoldController(
        ApplicationDbContext context,
        IPropertyVerificationService verificationService,
        IVerificationJobStatus jobStatus)
    {
        _context = context;
        _verificationService = verificationService;
        _jobStatus = jobStatus;
    }

    /// <summary>
    /// GET /api/properties/sold
    /// Lista paginada de propiedades vendidas.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetSoldProperties(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? city = null,
        [FromQuery] string? propertyType = null,
        [FromQuery] string? region = null,
        [FromQuery] string? sortBy = "soldDate",
        [FromQuery] string? sortDir = "desc")
    {
        var query = _context.Properties
            .Where(p => p.ListingStatus == "sold");

        // Filtros
        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(p => p.City == city);
        if (!string.IsNullOrWhiteSpace(propertyType))
            query = query.Where(p => p.PropertyType == propertyType);
        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(p => p.Region == region);

        var total = await query.CountAsync();

        // Ordenamiento
        query = (sortBy, sortDir) switch
        {
            ("soldDate", "asc") => query.OrderBy(p => p.SoldDetectedAt),
            ("soldDate", _)     => query.OrderByDescending(p => p.SoldDetectedAt),
            ("price", "asc")    => query.OrderBy(p => p.Price),
            ("price", _)        => query.OrderByDescending(p => p.Price),
            ("daysOnMarket", "asc") => query.OrderBy(p => p.SoldDetectedAt != null && p.FirstSeenAt != null
                ? (p.SoldDetectedAt.Value - p.FirstSeenAt.Value).Days : 0),
            ("daysOnMarket", _) => query.OrderByDescending(p => p.SoldDetectedAt != null && p.FirstSeenAt != null
                ? (p.SoldDetectedAt.Value - p.FirstSeenAt.Value).Days : 0),
            _ => query.OrderByDescending(p => p.SoldDetectedAt),
        };

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Price,
                p.Currency,
                p.City,
                p.Region,
                p.Neighborhood,
                p.PropertyType,
                p.Condition,
                p.Bedrooms,
                p.Bathrooms,
                p.Area,
                p.SourceUrl,
                p.FirstSeenAt,
                p.LastSeenAt,
                p.SoldDetectedAt,
                p.TimesScraped,
                p.ConsecutiveMisses,
                p.LastVerifiedAt,
                DaysOnMarket = p.SoldDetectedAt != null && p.FirstSeenAt != null
                    ? (p.SoldDetectedAt.Value - p.FirstSeenAt.Value).Days
                    : (int?)null,
            })
            .ToListAsync();

        return Ok(new
        {
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize),
            items,
        });
    }

    /// <summary>
    /// GET /api/properties/sold/stats
    /// KPIs de vendidos.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetSoldStats()
    {
        var now = DateTime.UtcNow;

        var totalSold = await _context.Properties
            .CountAsync(p => p.ListingStatus == "sold");

        var soldThisMonth = await _context.Properties
            .CountAsync(p => p.ListingStatus == "sold"
                          && p.SoldDetectedAt != null
                          && p.SoldDetectedAt >= new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc));

        var soldThisWeek = await _context.Properties
            .CountAsync(p => p.ListingStatus == "sold"
                          && p.SoldDetectedAt != null
                          && p.SoldDetectedAt >= now.AddDays(-7));

        // Tiempo promedio en mercado (solo propiedades con ambas fechas)
        var avgDaysRaw = await _context.Properties
            .Where(p => p.ListingStatus == "sold"
                     && p.SoldDetectedAt != null
                     && p.FirstSeenAt != null)
            .Select(p => (p.SoldDetectedAt!.Value - p.FirstSeenAt!.Value).TotalDays)
            .ToListAsync();

        var avgDaysOnMarket = avgDaysRaw.Count > 0 ? Math.Round(avgDaysRaw.Average(), 1) : 0;

        // Propiedades activas (para calcular tasa)
        var totalActive = await _context.Properties
            .CountAsync(p => p.ListingStatus == "active" || p.ListingStatus == "price_changed");

        // Pendientes de verificación (missing)
        var pendingVerification = await _context.Properties
            .CountAsync(p => p.ListingStatus == "missing");

        // Sin SourceUrl (no verificables)
        var withoutSourceUrl = await _context.Properties
            .CountAsync(p => p.SourceUrl == null && p.ListingStatus != "sold");

        // Vendidas por mes (últimos 6 meses)
        var sixMonthsAgo = now.AddMonths(-6);
        var soldByMonthRaw = await _context.Properties
            .Where(p => p.ListingStatus == "sold"
                     && p.SoldDetectedAt != null
                     && p.SoldDetectedAt >= sixMonthsAgo)
            .GroupBy(p => new { p.SoldDetectedAt!.Value.Year, p.SoldDetectedAt!.Value.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                count = g.Count(),
            })
            .OrderBy(g => g.Year).ThenBy(g => g.Month)
            .ToListAsync();

        var soldByMonth = soldByMonthRaw
            .Select(g => new { month = $"{g.Year}-{g.Month:D2}", g.count })
            .ToList();

        // Vendidas por zona (top 10)
        var soldByCity = await _context.Properties
            .Where(p => p.ListingStatus == "sold" && p.City != null)
            .GroupBy(p => p.City)
            .Select(g => new { city = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count)
            .Take(10)
            .ToListAsync();

        // Precio promedio de vendidas vs activas
        var avgPriceSold = await _context.Properties
            .Where(p => p.ListingStatus == "sold" && p.Price > 0)
            .AverageAsync(p => (double?)p.Price) ?? 0;

        var avgPriceActive = await _context.Properties
            .Where(p => (p.ListingStatus == "active" || p.ListingStatus == "price_changed") && p.Price > 0)
            .AverageAsync(p => (double?)p.Price) ?? 0;

        return Ok(new
        {
            totalSold,
            soldThisMonth,
            soldThisWeek,
            avgDaysOnMarket,
            totalActive,
            pendingVerification,
            withoutSourceUrl,
            saleRate = totalActive > 0
                ? Math.Round((double)soldThisWeek / totalActive * 100, 2)
                : 0,
            soldByMonth,
            soldByCity,
            avgPriceSold = Math.Round(avgPriceSold, 0),
            avgPriceActive = Math.Round(avgPriceActive, 0),
        });
    }

    /// <summary>
    /// GET /api/properties/sold/job-status
    /// Estado del último job de verificación.
    /// </summary>
    [HttpGet("job-status")]
    public IActionResult GetJobStatus()
    {
        var last = _jobStatus.LastRun;
        if (last == null)
            return Ok(new { hasRun = false });

        // Próxima ejecución: 3:00 AM UTC del día siguiente a la última ejecución
        var nextRun = last.RanAt.Date.AddDays(1).AddHours(3);

        return Ok(new
        {
            hasRun = true,
            ranAt = last.RanAt,
            verified = last.Verified,
            sold = last.Sold,
            active = last.Active,
            errors = last.Errors,
            nextRun,
        });
    }

    /// <summary>
    /// GET /api/properties/sold/verification-queue
    /// Propiedades pendientes de verificación (missing o stale sin verificar).
    /// </summary>
    [HttpGet("verification-queue")]
    public async Task<IActionResult> GetVerificationQueue(
        [FromQuery] int limit = 50)
    {
        var threeDaysAgo = DateTime.UtcNow.AddDays(-3);

        var queue = await _context.Properties
            .Where(p => p.SourceUrl != null
                     && p.ListingStatus != "sold"
                     && ((p.ListingStatus == "missing")
                         || (p.LastSeenAt != null && p.LastSeenAt < threeDaysAgo)))
            .OrderByDescending(p => p.ListingStatus == "missing" ? 1 : 0) // missing primero
            .ThenBy(p => p.LastVerifiedAt ?? DateTime.MinValue)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Price,
                p.Currency,
                p.City,
                p.PropertyType,
                p.SourceUrl,
                p.ListingStatus,
                p.LastSeenAt,
                p.LastVerifiedAt,
                p.ConsecutiveMisses,
                daysSinceLastSeen = p.LastSeenAt != null
                    ? (DateTime.UtcNow - p.LastSeenAt.Value).Days
                    : (int?)null,
            })
            .ToListAsync();

        return Ok(new { total = queue.Count, items = queue });
    }

    /// <summary>
    /// POST /api/properties/sold/{id}/verify
    /// Fuerza verificación HTTP de una propiedad.
    /// </summary>
    [HttpPost("{id}/verify")]
    public async Task<IActionResult> VerifyProperty(int id)
    {
        var property = await _context.Properties.FindAsync(id);
        if (property == null) return NotFound();
        if (string.IsNullOrWhiteSpace(property.SourceUrl))
            return BadRequest(new { error = "Propiedad sin SourceUrl, no se puede verificar." });

        var result = await _verificationService.VerifySinglePropertyAsync(id);

        // Recargar para obtener estado actualizado
        await _context.Entry(property).ReloadAsync();

        return Ok(new
        {
            propertyId = id,
            result = result.ToString(),
            listingStatus = property.ListingStatus,
            lastVerifiedAt = property.LastVerifiedAt,
            consecutiveMisses = property.ConsecutiveMisses,
        });
    }

    /// <summary>
    /// POST /api/properties/sold/{id}/mark-sold
    /// Marca manualmente como vendida.
    /// </summary>
    [HttpPost("{id}/mark-sold")]
    public async Task<IActionResult> MarkSold(int id)
    {
        var property = await _context.Properties.FindAsync(id);
        if (property == null) return NotFound();

        await _verificationService.MarkAsSoldAsync(id);
        return Ok(new { propertyId = id, listingStatus = "sold" });
    }

    /// <summary>
    /// POST /api/properties/sold/{id}/mark-active
    /// Reactivar (falso positivo).
    /// </summary>
    [HttpPost("{id}/mark-active")]
    public async Task<IActionResult> MarkActive(int id)
    {
        var property = await _context.Properties.FindAsync(id);
        if (property == null) return NotFound();

        await _verificationService.MarkAsActiveAsync(id);
        return Ok(new { propertyId = id, listingStatus = "active" });
    }
}
