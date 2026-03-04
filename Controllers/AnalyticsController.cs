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

    public AnalyticsController(ApplicationDbContext context)
    {
        _context = context;
    }

    // ─────────────────────────────────────────────────────────────────
    // GET /api/analytics/locations?region=&city=
    // Devuelve opciones para filtros. Soporta filtrado en cascada:
    // si se pasa region, devuelve solo las ciudades de esa región.
    // si se pasa city también, devuelve solo las comunas de esa ciudad.
    // ─────────────────────────────────────────────────────────────────
    [HttpGet("locations")]
    public async Task<IActionResult> GetLocations(
        [FromQuery] string? region = null,
        [FromQuery] string? city = null)
    {
        // Regiones: siempre todas (sin filtro)
        var regions = await _context.Properties
            .Where(p => p.Region != null && p.Region != "")
            .Select(p => p.Region!)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync();

        // Ciudades: filtradas por región si se especifica
        var cityQuery = _context.Properties.Where(p => p.City != null && p.City != "");
        if (!string.IsNullOrWhiteSpace(region))
            cityQuery = cityQuery.Where(p => p.Region == region);

        var cities = await cityQuery
            .Select(p => p.City!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        // Comunas: filtradas por región y ciudad si se especifican
        var neighborhoodQuery = _context.Properties.Where(p => p.Neighborhood != null && p.Neighborhood != "");
        if (!string.IsNullOrWhiteSpace(region))
            neighborhoodQuery = neighborhoodQuery.Where(p => p.Region == region);
        if (!string.IsNullOrWhiteSpace(city))
            neighborhoodQuery = neighborhoodQuery.Where(p => p.City == city);

        var neighborhoods = await neighborhoodQuery
            .Select(p => p.Neighborhood!)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync();

        // Tipos: siempre todos
        var propertyTypes = await _context.Properties
            .Where(p => p.PropertyType != null && p.PropertyType != "")
            .Select(p => p.PropertyType!)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();

        return Ok(new { regions, cities, neighborhoods, propertyTypes });
    }

    // ─────────────────────────────────────────────────────────────────
    // GET /api/analytics/market?region=&city=&neighborhood=&propertyType=
    // Retorna estadísticas agregadas del mercado (sin JSON masivo de propiedades).
    // ─────────────────────────────────────────────────────────────────
    [HttpGet("market")]
    public async Task<IActionResult> GetMarket(
        [FromQuery] string? region = null,
        [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null,
        [FromQuery] string? propertyType = null)
    {
        var query = _context.Properties.AsQueryable();
        query = ApplyFilters(query, region, city, neighborhood, propertyType);

        var totalProperties = await query.CountAsync();

        var byType = await query
            .Where(p => p.PropertyType != null)
            .GroupBy(p => p.PropertyType!)
            .Select(g => new
            {
                type = g.Key,
                count = g.Count(),
                avgPrice = g.Where(p => p.Price > 0).Any()
                    ? (double?)g.Where(p => p.Price > 0).Average(p => (double)p.Price!)
                    : null,
                avgBedrooms = g.Where(p => p.Bedrooms > 0).Any()
                    ? (double?)g.Where(p => p.Bedrooms > 0).Average(p => (double)p.Bedrooms!)
                    : null,
                avgBathrooms = g.Where(p => p.Bathrooms > 0).Any()
                    ? (double?)g.Where(p => p.Bathrooms > 0).Average(p => (double)p.Bathrooms!)
                    : null,
                avgArea = g.Where(p => p.Area > 0).Any()
                    ? (double?)g.Where(p => p.Area > 0).Average(p => (double)p.Area!)
                    : null,
                minPrice = g.Where(p => p.Price > 0).Any()
                    ? (double?)g.Where(p => p.Price > 0).Min(p => (double)p.Price!)
                    : null,
                maxPrice = g.Where(p => p.Price > 0).Any()
                    ? (double?)g.Where(p => p.Price > 0).Max(p => (double)p.Price!)
                    : null,
            })
            .OrderByDescending(g => g.count)
            .ToListAsync();

        var topNeighborhoods = await query
            .Where(p => p.Neighborhood != null || p.City != null)
            .GroupBy(p => p.Neighborhood ?? p.City ?? "Sin dato")
            .Select(g => new
            {
                name = g.Key,
                count = g.Count(),
                avgPrice = g.Where(p => p.Price > 0).Any()
                    ? (double?)g.Where(p => p.Price > 0).Average(p => (double)p.Price!)
                    : null,
            })
            .OrderByDescending(g => g.count)
            .Take(10)
            .ToListAsync();

        var globalAvgs = await query
            .Where(p => p.Price > 0 || p.Bedrooms > 0 || p.Bathrooms > 0 || p.Area > 0)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                avgPrice = g.Where(p => p.Price > 0).Any()
                    ? (double?)g.Where(p => p.Price > 0).Average(p => (double)p.Price!)
                    : null,
                avgBedrooms = g.Where(p => p.Bedrooms > 0).Any()
                    ? (double?)g.Where(p => p.Bedrooms > 0).Average(p => (double)p.Bedrooms!)
                    : null,
                avgBathrooms = g.Where(p => p.Bathrooms > 0).Any()
                    ? (double?)g.Where(p => p.Bathrooms > 0).Average(p => (double)p.Bathrooms!)
                    : null,
                avgArea = g.Where(p => p.Area > 0).Any()
                    ? (double?)g.Where(p => p.Area > 0).Average(p => (double)p.Area!)
                    : null,
            })
            .FirstOrDefaultAsync();

        // Distribución de precios por moneda
        var ufPrices = await query
            .Where(p => p.Currency == "UF" && p.Price > 0)
            .Select(p => (double)p.Price!)
            .ToListAsync();

        var clpPrices = await query
            .Where(p => p.Currency == "CLP" && p.Price > 0)
            .Select(p => (double)p.Price!)
            .ToListAsync();

        var ufDistribution = BuildPriceRanges(ufPrices, "UF");
        var clpDistribution = BuildPriceRanges(clpPrices, "CLP");

        // Sources (por fuente/bot)
        var sources = await _context.Bots
            .GroupBy(b => b.Source)
            .Select(g => new
            {
                name = g.Key,
                value = g.Sum(b => b.TotalScraped),
                count = g.Count()
            })
            .ToListAsync();

        return Ok(new
        {
            totalProperties,
            byType,
            topNeighborhoods,
            globalAverages = globalAvgs,
            priceDistribution = new { uf = ufDistribution, clp = clpDistribution },
            sources,
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // GET /api/analytics/trends?region=&city=&neighborhood=
    // Tendencias: propiedades scrapeadas por mes (basado en bots).
    // ─────────────────────────────────────────────────────────────────
    [HttpGet("trends")]
    public async Task<IActionResult> GetTrends(
        [FromQuery] string? region = null,
        [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null)
    {
        // Para trends usamos los datos de bots (ejecuciones mensuales)
        var bots = await _context.Bots
            .Where(b => b.LastRun.HasValue)
            .Select(b => new { b.LastRun, b.LastRunCount, b.Status })
            .ToListAsync();

        var trends = bots
            .GroupBy(b => new
            {
                Year = b.LastRun!.Value.Year,
                Month = b.LastRun!.Value.Month
            })
            .Select(g => new
            {
                mes = $"{g.Key.Year}-{g.Key.Month:D2}",
                exitosas = g.Count(b => b.Status == "completed"),
                fallidas = g.Count(b => b.Status == "error"),
                total = g.Count()
            })
            .OrderBy(t => t.mes)
            .ToList();

        return Ok(new { items = trends });
    }

    // ─────────────────────────────────────────────────────────────────
    // GET /api/analytics/compare?region=&city=&neighborhood=
    // Comparativa entre tipos de propiedad.
    // ─────────────────────────────────────────────────────────────────
    [HttpGet("compare")]
    public async Task<IActionResult> GetCompare(
        [FromQuery] string? region = null,
        [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null,
        [FromQuery] string? propertyType = null)
    {
        var query = _context.Properties.AsQueryable();
        query = ApplyFilters(query, region, city, neighborhood, propertyType);

        var byType = await query
            .Where(p => p.PropertyType != null)
            .GroupBy(p => p.PropertyType!)
            .Select(g => new
            {
                type = g.Key,
                count = g.Count(),
                avgPrice = g.Where(p => p.Price > 0).Any()
                    ? (double?)g.Where(p => p.Price > 0).Average(p => (double)p.Price!)
                    : null,
                avgBedrooms = g.Where(p => p.Bedrooms > 0).Any()
                    ? (double?)g.Where(p => p.Bedrooms > 0).Average(p => (double)p.Bedrooms!)
                    : null,
                avgBathrooms = g.Where(p => p.Bathrooms > 0).Any()
                    ? (double?)g.Where(p => p.Bathrooms > 0).Average(p => (double)p.Bathrooms!)
                    : null,
                avgArea = g.Where(p => p.Area > 0).Any()
                    ? (double?)g.Where(p => p.Area > 0).Average(p => (double)p.Area!)
                    : null,
            })
            .OrderByDescending(g => g.count)
            .ToListAsync();

        var sources = await _context.Bots
            .GroupBy(b => b.Source)
            .Select(g => new
            {
                name = g.Key,
                value = g.Sum(b => b.TotalScraped),
            })
            .ToListAsync();

        return Ok(new { byType, sources });
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static IQueryable<Inmobiscrap.Models.Property> ApplyFilters(
        IQueryable<Inmobiscrap.Models.Property> query,
        string? region,
        string? city,
        string? neighborhood,
        string? propertyType)
    {
        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(p => p.Region == region);

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrWhiteSpace(neighborhood))
            query = query.Where(p => p.Neighborhood == neighborhood);

        if (!string.IsNullOrWhiteSpace(propertyType))
            query = query.Where(p => p.PropertyType == propertyType);

        return query;
    }

    private static object BuildPriceRanges(List<double> prices, string currency)
    {
        if (prices.Count == 0) return new object[] { };

        if (currency == "UF")
        {
            var ranges = new[]
            {
                new { label = "< 1.000", min = 0.0, max = 1000.0 },
                new { label = "1K - 2K",  min = 1000.0, max = 2000.0 },
                new { label = "2K - 3K",  min = 2000.0, max = 3000.0 },
                new { label = "3K - 5K",  min = 3000.0, max = 5000.0 },
                new { label = "5K - 8K",  min = 5000.0, max = 8000.0 },
                new { label = "8K - 15K", min = 8000.0, max = 15000.0 },
                new { label = "> 15K",    min = 15000.0, max = double.MaxValue },
            };
            return ranges
                .Select(r => new { rango = r.label, cantidad = prices.Count(p => p >= r.min && p < r.max) })
                .Where(r => r.cantidad > 0);
        }
        else
        {
            var ranges = new[]
            {
                new { label = "< 50M",       min = 0.0,           max = 50_000_000.0 },
                new { label = "50M - 80M",   min = 50_000_000.0,  max = 80_000_000.0 },
                new { label = "80M - 120M",  min = 80_000_000.0,  max = 120_000_000.0 },
                new { label = "120M - 180M", min = 120_000_000.0, max = 180_000_000.0 },
                new { label = "180M - 300M", min = 180_000_000.0, max = 300_000_000.0 },
                new { label = "> 300M",      min = 300_000_000.0, max = double.MaxValue },
            };
            return ranges
                .Select(r => new { rango = r.label, cantidad = prices.Count(p => p >= r.min && p < r.max) })
                .Where(r => r.cantidad > 0);
        }
    }
}