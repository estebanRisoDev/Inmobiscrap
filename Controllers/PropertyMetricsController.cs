using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Microsoft.AspNetCore.Authorization;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/metrics")]
[Authorize]
public class PropertyMetricsController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public PropertyMetricsController(ApplicationDbContext context) => _context = context;

    private int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : 0;
    }

    private async Task<IActionResult?> ConsumeCredits(int amount = 5)
    {
        var userId = GetUserId();

        // Lectura liviana solo para verificar plan/rol y tener el conteo actual para el mensaje de error
        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Plan, u.Role, u.Credits })
            .FirstOrDefaultAsync();

        if (user == null) return Unauthorized();
        if (user.Plan == "pro" || user.Role == "admin") return null;

        // Decremento atómico: solo ejecuta si Credits >= amount en la misma operación SQL
        // Elimina la race condition del patrón read-then-write anterior
        var rows = await _context.Users
            .Where(u => u.Id == userId && u.Credits >= amount)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Credits, u => u.Credits - amount));

        if (rows == 0)
            return StatusCode(402, new { message = $"Créditos insuficientes. Necesitas {amount}, tienes {user.Credits}.", credits = user.Credits, plan = user.Plan });

        HttpContext.Response.Headers.Append("X-Credits-Remaining", (user.Credits - amount).ToString());
        return null;
    }

    // GET /api/metrics/general
    [HttpGet("general")]
    public async Task<IActionResult> GetGeneralMetrics(
        [FromQuery] string? region = null, [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null, [FromQuery] string? propertyType = null)
    {
        var check = await ConsumeCredits();
        if (check != null) return check;

        var query = ApplyFilters(_context.Properties.AsQueryable(), region, city, neighborhood, propertyType);
        var now = DateTime.UtcNow;

        var totalProperties = await query.CountAsync();

        // Condition distribution (Nuevo/Usado)
        var conditionDistribution = await query
            .Where(p => p.Condition != null && p.Condition != "")
            .GroupBy(p => p.Condition!)
            .Select(g => new { condition = g.Key, count = g.Count() })
            .ToListAsync();

        // Publication time stats
        var propertiesWithPubDate = await query
            .Where(p => p.PublicationDate.HasValue)
            .Select(p => new { p.PublicationDate, p.Price, p.Currency, p.PropertyType, p.Condition })
            .ToListAsync();

        var avgDaysOnMarket = propertiesWithPubDate.Count > 0
            ? propertiesWithPubDate.Average(p => (now - p.PublicationDate!.Value).TotalDays)
            : (double?)null;

        var publicationByMonth = propertiesWithPubDate
            .GroupBy(p => new { p.PublicationDate!.Value.Year, p.PublicationDate!.Value.Month })
            .Select(g => new
            {
                month = $"{g.Key.Year}-{g.Key.Month:D2}",
                count = g.Count()
            })
            .OrderBy(x => x.month)
            .ToList();

        // Average price by condition
        var priceByCondition = await query
            .Where(p => p.Condition != null && p.Condition != "" && p.Price > 0)
            .GroupBy(p => new { Condition = p.Condition!, Currency = p.Currency ?? "CLP" })
            .Select(g => new
            {
                condition = g.Key.Condition!,
                currency = g.Key.Currency,
                count = g.Count(),
                avgPrice = g.Average(p => (double)p.Price!),
                minPrice = g.Min(p => (double)p.Price!),
                maxPrice = g.Max(p => (double)p.Price!),
            })
            .ToListAsync();

        // Average price by publication age range
        var priceByAge = propertiesWithPubDate
            .Where(p => p.Price > 0)
            .Select(p => new { days = (now - p.PublicationDate!.Value).TotalDays, price = (double)p.Price!, currency = p.Currency ?? "CLP" })
            .GroupBy(p => p.days switch
            {
                < 7 => "< 1 semana",
                < 30 => "1-4 semanas",
                < 90 => "1-3 meses",
                < 180 => "3-6 meses",
                _ => "> 6 meses"
            })
            .Select(g => new
            {
                range = g.Key,
                count = g.Count(),
                avgPrice = g.Average(p => p.price),
            })
            .ToList();

        // Condition by property type
        var conditionByType = await query
            .Where(p => p.Condition != null && p.Condition != "" && p.PropertyType != null)
            .GroupBy(p => new { PropertyType = p.PropertyType!, Condition = p.Condition! })
            .Select(g => new
            {
                propertyType = g.Key.PropertyType!,
                condition = g.Key.Condition!,
                count = g.Count()
            })
            .ToListAsync();

        // Price per square meter
        var pricePerSqmData = await query
            .Where(p => p.Price > 0 && p.Area > 0)
            .Select(p => new { p.Price, p.Area, Currency = p.Currency ?? "CLP", p.PropertyType, p.City, p.Condition })
            .ToListAsync();

        var avgPricePerSqm = pricePerSqmData
            .GroupBy(p => p.Currency)
            .Select(g => new
            {
                currency = g.Key,
                count = g.Count(),
                avg = Math.Round(g.Average(p => (double)(p.Price! / p.Area!)), 0),
                min = Math.Round(g.Min(p => (double)(p.Price! / p.Area!)), 0),
                max = Math.Round(g.Max(p => (double)(p.Price! / p.Area!)), 0),
            })
            .ToList();

        var pricePerSqmByType = pricePerSqmData
            .Where(p => p.PropertyType != null)
            .GroupBy(p => new { PropertyType = p.PropertyType!, Currency = p.Currency })
            .Select(g => new
            {
                propertyType = g.Key.PropertyType,
                currency = g.Key.Currency,
                count = g.Count(),
                avg = Math.Round(g.Average(p => (double)(p.Price! / p.Area!)), 0),
            })
            .ToList();

        var pricePerSqmByCity = pricePerSqmData
            .Where(p => p.City != null)
            .GroupBy(p => new { City = p.City!, Currency = p.Currency })
            .Select(g => new
            {
                city = g.Key.City,
                currency = g.Key.Currency,
                count = g.Count(),
                avg = Math.Round(g.Average(p => (double)(p.Price! / p.Area!)), 0),
            })
            .OrderByDescending(x => x.avg)
            .Take(20)
            .ToList();

        var pricePerSqmByCondition = pricePerSqmData
            .Where(p => p.Condition != null && p.Condition != "")
            .GroupBy(p => new { Condition = p.Condition!, Currency = p.Currency })
            .Select(g => new
            {
                condition = g.Key.Condition,
                currency = g.Key.Currency,
                count = g.Count(),
                avg = Math.Round(g.Average(p => (double)(p.Price! / p.Area!)), 0),
            })
            .ToList();

        return Ok(new
        {
            totalProperties,
            conditionDistribution,
            avgDaysOnMarket = avgDaysOnMarket.HasValue ? Math.Round(avgDaysOnMarket.Value, 1) : (double?)null,
            publicationByMonth,
            priceByCondition,
            priceByAge,
            conditionByType,
            propertiesWithPubDate = propertiesWithPubDate.Count,
            propertiesWithCondition = conditionDistribution.Sum(c => c.count),
            avgPricePerSqm,
            pricePerSqmByType,
            pricePerSqmByCity,
            pricePerSqmByCondition,
        });
    }

    // GET /api/metrics/properties
    [HttpGet("properties")]
    public async Task<IActionResult> GetPropertyList(
        [FromQuery] string? region = null, [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null, [FromQuery] string? propertyType = null,
        [FromQuery] string? condition = null,
        [FromQuery] string? currency = null,
        [FromQuery] bool? isArriendo = null,
        // Column-level filters
        [FromQuery] string?  titleSearch        = null,
        [FromQuery] string?  citySearch         = null,
        [FromQuery] string?  propertyTypeSearch = null,
        [FromQuery] string?  conditionSearch    = null,
        [FromQuery] decimal? priceMin           = null, [FromQuery] decimal? priceMax        = null,
        [FromQuery] int?     bedrooms           = null,
        [FromQuery] int?     bathrooms          = null,
        [FromQuery] decimal? areaMin            = null, [FromQuery] decimal? areaMax         = null,
        [FromQuery] decimal? pricePerSqmMin     = null, [FromQuery] decimal? pricePerSqmMax  = null,
        [FromQuery] DateTime? pubDateFrom       = null, [FromQuery] DateTime? pubDateTo       = null,
        [FromQuery] DateTime? firstSeenFrom     = null, [FromQuery] DateTime? firstSeenTo     = null,
        [FromQuery] string?  sortBy = "price", [FromQuery] string? sortDir = "asc",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var check = await ConsumeCredits();
        if (check != null) return check;

        var query = ApplyFilters(_context.Properties.AsQueryable(), region, city, neighborhood, propertyType);

        if (!string.IsNullOrWhiteSpace(condition))
            query = query.Where(p => p.Condition == condition);
        if (!string.IsNullOrWhiteSpace(currency))
            query = query.Where(p => p.Currency == currency);
        if (isArriendo.HasValue)
            query = query.Where(p => p.IsArriendo == isArriendo.Value);

        // Column-level text filters (case-insensitive via ILike)
        if (!string.IsNullOrWhiteSpace(titleSearch))
            query = query.Where(p => p.Title != null && EF.Functions.ILike(p.Title, $"%{titleSearch.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(citySearch))
            query = query.Where(p => p.City != null && EF.Functions.ILike(p.City, $"%{citySearch.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(propertyTypeSearch))
            query = query.Where(p => p.PropertyType != null && EF.Functions.ILike(p.PropertyType, $"%{propertyTypeSearch.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(conditionSearch))
            query = query.Where(p => p.Condition != null && EF.Functions.ILike(p.Condition, $"%{conditionSearch.Trim()}%"));

        // Column-level numeric filters
        if (priceMin.HasValue)     query = query.Where(p => p.Price >= priceMin.Value);
        if (priceMax.HasValue)     query = query.Where(p => p.Price <= priceMax.Value);
        if (bedrooms.HasValue)  query = query.Where(p => p.Bedrooms == bedrooms.Value);
        if (bathrooms.HasValue) query = query.Where(p => p.Bathrooms == bathrooms.Value);
        if (areaMin.HasValue)      query = query.Where(p => p.Area >= areaMin.Value);
        if (areaMax.HasValue)      query = query.Where(p => p.Area <= areaMax.Value);
        if (pricePerSqmMin.HasValue)
            query = query.Where(p => p.Price > 0 && p.Area > 0 && p.Price / p.Area >= pricePerSqmMin.Value);
        if (pricePerSqmMax.HasValue)
            query = query.Where(p => p.Price > 0 && p.Area > 0 && p.Price / p.Area <= pricePerSqmMax.Value);

        // Column-level date filters
        if (pubDateFrom.HasValue)   query = query.Where(p => p.PublicationDate >= pubDateFrom.Value);
        if (pubDateTo.HasValue)     query = query.Where(p => p.PublicationDate <= pubDateTo.Value);
        if (firstSeenFrom.HasValue) query = query.Where(p => p.FirstSeenAt >= firstSeenFrom.Value);
        if (firstSeenTo.HasValue)   query = query.Where(p => p.FirstSeenAt <= firstSeenTo.Value);

        var totalCount = await query.CountAsync();

        // Sorting
        query = (sortBy?.ToLower(), sortDir?.ToLower()) switch
        {
            ("price", "desc") => query.OrderByDescending(p => p.Price),
            ("price", _) => query.OrderBy(p => p.Price),
            ("area", "desc") => query.OrderByDescending(p => p.Area),
            ("area", _) => query.OrderBy(p => p.Area),
            ("bedrooms", "desc") => query.OrderByDescending(p => p.Bedrooms),
            ("bedrooms", _) => query.OrderBy(p => p.Bedrooms),
            ("bathrooms", "desc") => query.OrderByDescending(p => p.Bathrooms),
            ("bathrooms", _) => query.OrderBy(p => p.Bathrooms),
            ("publicationdate", "desc") => query.OrderByDescending(p => p.PublicationDate),
            ("publicationdate", _) => query.OrderBy(p => p.PublicationDate),
            ("firstseenat", "desc") => query.OrderByDescending(p => p.FirstSeenAt),
            ("firstseenat", _) => query.OrderBy(p => p.FirstSeenAt),
            ("title", "desc") => query.OrderByDescending(p => p.Title),
            ("title", _) => query.OrderBy(p => p.Title),
            ("pricepersqm", "desc") => query.OrderByDescending(p => p.Price > 0 && p.Area > 0 ? p.Price / p.Area : 0),
            ("pricepersqm", _) => query.OrderBy(p => p.Price > 0 && p.Area > 0 ? p.Price / p.Area : decimal.MaxValue),
            _ => query.OrderBy(p => p.Price),
        };

        var properties = await query
            .Skip((page - 1) * pageSize)
            .Take(Math.Min(pageSize, 100))
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Price,
                p.Currency,
                p.Bedrooms,
                p.Bathrooms,
                p.Area,
                p.PropertyType,
                p.City,
                p.Region,
                p.Neighborhood,
                p.Condition,
                p.PublicationDate,
                p.FirstSeenAt,
                p.LastSeenAt,
                p.TimesScraped,
                p.ListingStatus,
                p.SourceUrl,
                p.IsArriendo,
                daysOnMarket = p.PublicationDate.HasValue
                    ? (int?)Math.Round((DateTime.UtcNow - p.PublicationDate.Value).TotalDays)
                    : p.FirstSeenAt.HasValue
                        ? (int?)Math.Round((DateTime.UtcNow - p.FirstSeenAt.Value).TotalDays)
                        : null,
                daysOnMarketSource = p.PublicationDate.HasValue ? "publicationDate" : p.FirstSeenAt.HasValue ? "firstSeenAt" : null,
                pricePerSqm = p.Price > 0 && p.Area > 0
                    ? (decimal?)Math.Round(p.Price.Value / p.Area.Value, 0)
                    : null,
            })
            .ToListAsync();

        return Ok(new
        {
            items = properties,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
        });
    }

    // GET /api/metrics/compare
    [HttpGet("compare")]
    public async Task<IActionResult> CompareProperties([FromQuery] string ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return BadRequest(new { message = "Provide comma-separated property IDs" });

        var idList = ids.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .Take(20)
            .ToList();

        if (idList.Count < 2)
            return BadRequest(new { message = "At least 2 property IDs required" });

        // 5 créditos por cada propiedad seleccionada
        var check = await ConsumeCredits(idList.Count * 5);
        if (check != null) return check;

        var properties = await _context.Properties
            .Where(p => idList.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Price,
                p.Currency,
                p.Bedrooms,
                p.Bathrooms,
                p.Area,
                p.PropertyType,
                p.City,
                p.Neighborhood,
                p.Condition,
                p.PublicationDate,
                p.FirstSeenAt,
                p.LastSeenAt,
                p.TimesScraped,
                p.ListingStatus,
                p.PreviousPrice,
                p.PriceChangedAt,
                p.IsArriendo,
                daysOnMarket = p.PublicationDate.HasValue
                    ? (int?)Math.Round((DateTime.UtcNow - p.PublicationDate.Value).TotalDays)
                    : p.FirstSeenAt.HasValue
                        ? (int?)Math.Round((DateTime.UtcNow - p.FirstSeenAt.Value).TotalDays)
                        : null,
                daysOnMarketSource = p.PublicationDate.HasValue ? "publicationDate" : p.FirstSeenAt.HasValue ? "firstSeenAt" : null,
                pricePerSqm = p.Price > 0 && p.Area > 0
                    ? (decimal?)Math.Round(p.Price.Value / p.Area.Value, 0)
                    : null,
            })
            .ToListAsync();

        return Ok(new { properties });
    }

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
}
