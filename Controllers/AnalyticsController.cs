using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Microsoft.AspNetCore.Authorization;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize]
public class AnalyticsController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public AnalyticsController(ApplicationDbContext context) => _context = context;

    private int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : 0;
    }

    private async Task<IActionResult?> ConsumeCredit()
    {
        var userId = GetUserId();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (user.Plan == "pro" || user.Role == "admin") return null;

        if (user.Credits <= 0)
        {
            return StatusCode(402, new
            {
                message = "Sin créditos. Actualiza a Pro para consultas ilimitadas.",
                credits = 0,
                plan = user.Plan,
            });
        }

        user.Credits--;
        await _context.SaveChangesAsync();
        HttpContext.Response.Headers.Append("X-Credits-Remaining", user.Credits.ToString());
        return null;
    }

    // GET /api/analytics/locations
    [HttpGet("locations")]
    public async Task<IActionResult> GetLocations(
        [FromQuery] string? region = null,
        [FromQuery] string? city = null)
    {
        var regions = await _context.Properties
            .Where(p => p.Region != null && p.Region != "")
            .Select(p => p.Region!).Distinct().OrderBy(r => r).ToListAsync();

        var cityQuery = _context.Properties.Where(p => p.City != null && p.City != "");
        if (!string.IsNullOrWhiteSpace(region))
            cityQuery = cityQuery.Where(p => p.Region == region);
        var cities = await cityQuery.Select(p => p.City!).Distinct().OrderBy(c => c).ToListAsync();

        var nhQuery = _context.Properties.Where(p => p.Neighborhood != null && p.Neighborhood != "");
        if (!string.IsNullOrWhiteSpace(region)) nhQuery = nhQuery.Where(p => p.Region == region);
        if (!string.IsNullOrWhiteSpace(city))   nhQuery = nhQuery.Where(p => p.City == city);
        var neighborhoods = await nhQuery.Select(p => p.Neighborhood!).Distinct().OrderBy(n => n).ToListAsync();

        var propertyTypes = await _context.Properties
            .Where(p => p.PropertyType != null && p.PropertyType != "")
            .Select(p => p.PropertyType!).Distinct().OrderBy(t => t).ToListAsync();

        return Ok(new { regions, cities, neighborhoods, propertyTypes });
    }

    // GET /api/analytics/market
    [HttpGet("market")]
    public async Task<IActionResult> GetMarket(
        [FromQuery] string? region = null, [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null, [FromQuery] string? propertyType = null)
    {
        var check = await ConsumeCredit();
        if (check != null) return check;

        var query = ApplyFilters(_context.Properties.AsQueryable(), region, city, neighborhood, propertyType);

        var totalProperties = await query.CountAsync();

        // ── FIX: Agrupar por (PropertyType, Currency) para nunca mezclar UF con CLP ──
        // Luego en memoria elegimos la moneda dominante por tipo de propiedad.
        var byTypeRaw = await query
            .Where(p => p.PropertyType != null)
            .GroupBy(p => new { p.PropertyType, Currency = p.Currency ?? "CLP" })
            .Select(g => new
            {
                type         = g.Key.PropertyType!,
                currency     = g.Key.Currency,
                count        = g.Count(),
                avgPrice     = g.Where(p => p.Price > 0).Any()
                                ? (double?)g.Where(p => p.Price > 0).Average(p => (double)p.Price!)
                                : null,
                avgBedrooms  = g.Where(p => p.Bedrooms > 0).Any()
                                ? (double?)g.Where(p => p.Bedrooms > 0).Average(p => (double)p.Bedrooms!)
                                : null,
                avgBathrooms = g.Where(p => p.Bathrooms > 0).Any()
                                ? (double?)g.Where(p => p.Bathrooms > 0).Average(p => (double)p.Bathrooms!)
                                : null,
                avgArea      = g.Where(p => p.Area > 0).Any()
                                ? (double?)g.Where(p => p.Area > 0).Average(p => (double)p.Area!)
                                : null,
                minPrice     = g.Where(p => p.Price > 0).Any()
                                ? (double?)g.Where(p => p.Price > 0).Min(p => (double)p.Price!)
                                : null,
                maxPrice     = g.Where(p => p.Price > 0).Any()
                                ? (double?)g.Where(p => p.Price > 0).Max(p => (double)p.Price!)
                                : null,
            })
            .OrderByDescending(g => g.count)
            .ToListAsync();

        // En memoria: por cada PropertyType, elegir la moneda con más propiedades.
        // Esto evita que un tipo aparezca duplicado (ej. Casa-UF y Casa-CLP) y
        // garantiza que avgPrice/minPrice/maxPrice correspondan a una sola moneda.
        var byType = byTypeRaw
            .GroupBy(g => g.type)
            .Select(g =>
            {
                // Moneda dominante = la que tiene más registros con precio
                var dominant = g.OrderByDescending(x => x.count).First();

                return new
                {
                    type         = dominant.type,
                    currency     = dominant.currency,
                    count        = g.Sum(x => x.count),          // total real = suma de ambas monedas
                    avgPrice     = dominant.avgPrice,             // precio solo de la moneda dominante
                    avgBedrooms  = dominant.avgBedrooms
                                   ?? g.FirstOrDefault(x => x.avgBedrooms.HasValue)?.avgBedrooms,
                    avgBathrooms = dominant.avgBathrooms
                                   ?? g.FirstOrDefault(x => x.avgBathrooms.HasValue)?.avgBathrooms,
                    avgArea      = dominant.avgArea
                                   ?? g.FirstOrDefault(x => x.avgArea.HasValue)?.avgArea,
                    minPrice     = dominant.minPrice,
                    maxPrice     = dominant.maxPrice,
                    // Extra: exponer cuántas propiedades había en cada moneda (útil para debug)
                    currencyBreakdown = g.Select(x => new { x.currency, x.count }).ToList(),
                };
            })
            .OrderByDescending(g => g.count)
            .ToList();

        var topNeighborhoods = await query
            .Where(p => p.Neighborhood != null || p.City != null)
            .GroupBy(p => p.Neighborhood ?? p.City ?? "Sin dato")
            .Select(g => new
            {
                name     = g.Key,
                count    = g.Count(),
                avgPrice = g.Where(p => p.Price > 0).Any()
                            ? (double?)g.Where(p => p.Price > 0).Average(p => (double)p.Price!)
                            : null,
            })
            .OrderByDescending(g => g.count).Take(10).ToListAsync();

        var globalAvgs = await query
            .Where(p => p.Price > 0 || p.Bedrooms > 0 || p.Bathrooms > 0 || p.Area > 0)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                avgPrice     = g.Where(p => p.Price > 0).Any()
                                ? (double?)g.Where(p => p.Price > 0).Average(p => (double)p.Price!)
                                : null,
                avgBedrooms  = g.Where(p => p.Bedrooms > 0).Any()
                                ? (double?)g.Where(p => p.Bedrooms > 0).Average(p => (double)p.Bedrooms!)
                                : null,
                avgBathrooms = g.Where(p => p.Bathrooms > 0).Any()
                                ? (double?)g.Where(p => p.Bathrooms > 0).Average(p => (double)p.Bathrooms!)
                                : null,
                avgArea      = g.Where(p => p.Area > 0).Any()
                                ? (double?)g.Where(p => p.Area > 0).Average(p => (double)p.Area!)
                                : null,
            })
            .FirstOrDefaultAsync();

        var ufPrices  = await query.Where(p => p.Currency == "UF"  && p.Price > 0).Select(p => (double)p.Price!).ToListAsync();
        var clpPrices = await query.Where(p => p.Currency == "CLP" && p.Price > 0).Select(p => (double)p.Price!).ToListAsync();

        var sources = await _context.Bots
            .GroupBy(b => b.Source)
            .Select(g => new { name = g.Key, value = g.Sum(b => b.TotalScraped), count = g.Count() })
            .ToListAsync();

        return Ok(new
        {
            totalProperties,
            byType,
            topNeighborhoods,
            globalAverages     = globalAvgs,
            priceDistribution  = new { uf = BuildPriceRanges(ufPrices, "UF"), clp = BuildPriceRanges(clpPrices, "CLP") },
            sources,
        });
    }

    // GET /api/analytics/trends
    [HttpGet("trends")]
    public async Task<IActionResult> GetTrends(
        [FromQuery] string? region = null, [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null)
    {
        var check = await ConsumeCredit();
        if (check != null) return check;

        var bots = await _context.Bots
            .Where(b => b.LastRun.HasValue)
            .Select(b => new { b.LastRun, b.LastRunCount, b.Status })
            .ToListAsync();

        var trends = bots
            .GroupBy(b => new { b.LastRun!.Value.Year, b.LastRun!.Value.Month })
            .Select(g => new
            {
                mes      = $"{g.Key.Year}-{g.Key.Month:D2}",
                exitosas = g.Count(b => b.Status == "completed"),
                fallidas = g.Count(b => b.Status == "error"),
                total    = g.Count()
            })
            .OrderBy(t => t.mes).ToList();

        return Ok(new { items = trends });
    }

    // GET /api/analytics/compare
    [HttpGet("compare")]
    public async Task<IActionResult> GetCompare(
        [FromQuery] string? region = null, [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null, [FromQuery] string? propertyType = null)
    {
        var check = await ConsumeCredit();
        if (check != null) return check;

        var query = ApplyFilters(_context.Properties.AsQueryable(), region, city, neighborhood, propertyType);

        // FIX: mismo patrón que GetMarket — agrupar por (type, currency)
        var byTypeRaw = await query
            .Where(p => p.PropertyType != null)
            .GroupBy(p => new { p.PropertyType, Currency = p.Currency ?? "CLP" })
            .Select(g => new
            {
                type         = g.Key.PropertyType!,
                currency     = g.Key.Currency,
                count        = g.Count(),
                avgPrice     = g.Where(p => p.Price > 0).Any()
                                ? (double?)g.Where(p => p.Price > 0).Average(p => (double)p.Price!)
                                : null,
                avgBedrooms  = g.Where(p => p.Bedrooms > 0).Any()
                                ? (double?)g.Where(p => p.Bedrooms > 0).Average(p => (double)p.Bedrooms!)
                                : null,
                avgBathrooms = g.Where(p => p.Bathrooms > 0).Any()
                                ? (double?)g.Where(p => p.Bathrooms > 0).Average(p => (double)p.Bathrooms!)
                                : null,
                avgArea      = g.Where(p => p.Area > 0).Any()
                                ? (double?)g.Where(p => p.Area > 0).Average(p => (double)p.Area!)
                                : null,
            })
            .ToListAsync();

        var byType = byTypeRaw
            .GroupBy(g => g.type)
            .Select(g =>
            {
                var dominant = g.OrderByDescending(x => x.count).First();
                return new
                {
                    type         = dominant.type,
                    currency     = dominant.currency,
                    count        = g.Sum(x => x.count),
                    avgPrice     = dominant.avgPrice,
                    avgBedrooms  = dominant.avgBedrooms,
                    avgBathrooms = dominant.avgBathrooms,
                    avgArea      = dominant.avgArea,
                };
            })
            .OrderByDescending(g => g.count)
            .ToList();

        var sources = await _context.Bots.GroupBy(b => b.Source)
            .Select(g => new { name = g.Key, value = g.Sum(b => b.TotalScraped) }).ToListAsync();

        return Ok(new { byType, sources });
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static IQueryable<Inmobiscrap.Models.Property> ApplyFilters(
        IQueryable<Inmobiscrap.Models.Property> q,
        string? region, string? city, string? neighborhood, string? propertyType)
    {
        if (!string.IsNullOrWhiteSpace(region))       q = q.Where(p => p.Region == region);
        if (!string.IsNullOrWhiteSpace(city))         q = q.Where(p => p.City == city);
        if (!string.IsNullOrWhiteSpace(neighborhood)) q = q.Where(p => p.Neighborhood == neighborhood);
        if (!string.IsNullOrWhiteSpace(propertyType)) q = q.Where(p => p.PropertyType == propertyType);
        return q;
    }

    private static object BuildPriceRanges(List<double> prices, string currency)
    {
        if (prices.Count == 0) return Array.Empty<object>();
        var ranges = currency == "UF"
            ? new[] {
                ("< 1.000",   0.0,      1000.0),
                ("1K - 2K",   1000.0,   2000.0),
                ("2K - 3K",   2000.0,   3000.0),
                ("3K - 5K",   3000.0,   5000.0),
                ("5K - 8K",   5000.0,   8000.0),
                ("8K - 15K",  8000.0,  15000.0),
                ("> 15K",    15000.0,  double.MaxValue) }
            : new[] {
                ("< 50M",        0.0,    50e6),
                ("50M - 80M",   50e6,    80e6),
                ("80M - 120M",  80e6,   120e6),
                ("120M - 180M",120e6,   180e6),
                ("180M - 300M",180e6,   300e6),
                ("> 300M",     300e6,  double.MaxValue) };

        return ranges
            .Select(r => new { rango = r.Item1, cantidad = prices.Count(p => p >= r.Item2 && p < r.Item3) })
            .Where(r => r.cantidad > 0);
    }
}