using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Inmobiscrap.Models;
using Microsoft.AspNetCore.Authorization;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PropertiesController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public PropertiesController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: api/properties?region=&city=&neighborhood=&propertyType=&page=1&pageSize=50
    [HttpGet]
    public async Task<ActionResult> GetProperties(
        [FromQuery] string? region = null,
        [FromQuery] string? city = null,
        [FromQuery] string? neighborhood = null,
        [FromQuery] string? propertyType = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        // Clamp page size para no devolver miles de registros
        pageSize = Math.Clamp(pageSize, 1, 500);
        page = Math.Max(1, page);

        var query = _context.Properties.AsQueryable();

        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(p => p.Region == region);

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrWhiteSpace(neighborhood))
            query = query.Where(p => p.Neighborhood == neighborhood);

        if (!string.IsNullOrWhiteSpace(propertyType))
            query = query.Where(p => p.PropertyType == propertyType);

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new
        {
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)total / pageSize),
            items
        });
    }

    // GET: api/properties/5
    [HttpGet("{id}")]
    public async Task<ActionResult<Property>> GetProperty(int id)
    {
        var property = await _context.Properties.FindAsync(id);
        if (property == null) return NotFound();
        return Ok(property);
    }

    // POST: api/properties
    [HttpPost]
    public async Task<ActionResult<Property>> CreateProperty(Property property)
    {
        _context.Properties.Add(property);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetProperty), new { id = property.Id }, property);
    }

    // PUT: api/properties/5
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProperty(int id, Property property)
    {
        if (id != property.Id) return BadRequest();

        _context.Entry(property).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await PropertyExists(id)) return NotFound();
            throw;
        }

        return NoContent();
    }

    // DELETE: api/properties/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProperty(int id)
    {
        var property = await _context.Properties.FindAsync(id);
        if (property == null) return NotFound();

        _context.Properties.Remove(property);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private async Task<bool> PropertyExists(int id) =>
        await _context.Properties.AnyAsync(e => e.Id == id);
}