using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Inmobiscrap.Models;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PropertiesController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public PropertiesController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: api/properties
    [HttpGet]
    public async Task<ActionResult<List<Property>>> GetProperties(
        [FromQuery] string? city = null,
        [FromQuery] string? propertyType = null)
    {
        var query = _context.Properties.AsQueryable();

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(propertyType))
            query = query.Where(p => p.PropertyType == propertyType);

        var properties = await query.ToListAsync();
        return Ok(properties);
    }

    // GET: api/properties/5
    [HttpGet("{id}")]
    public async Task<ActionResult<Property>> GetProperty(int id)
    {
        var property = await _context.Properties.FindAsync(id);

        if (property == null)
            return NotFound();

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
        if (id != property.Id)
            return BadRequest();

        _context.Entry(property).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await PropertyExists(id))
                return NotFound();
            throw;
        }

        return NoContent();
    }

    // DELETE: api/properties/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProperty(int id)
    {
        var property = await _context.Properties.FindAsync(id);
        if (property == null)
            return NotFound();

        _context.Properties.Remove(property);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private async Task<bool> PropertyExists(int id)
    {
        return await _context.Properties.AnyAsync(e => e.Id == id);
    }
}