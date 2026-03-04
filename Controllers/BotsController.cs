using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Inmobiscrap.Data;
using Inmobiscrap.Models;
using Inmobiscrap.Jobs;
using Microsoft.AspNetCore.Authorization;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BotsController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public BotsController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: api/bots
    [HttpGet]
    public async Task<ActionResult<List<Bot>>> GetBots()
    {
        return await _context.Bots.ToListAsync();
    }

    // GET: api/bots/5
    [HttpGet("{id}")]
    public async Task<ActionResult<Bot>> GetBot(int id)
    {
        var bot = await _context.Bots.FindAsync(id);
        if (bot == null) return NotFound();
        return Ok(bot);
    }

    // POST: api/bots
    [HttpPost]
    public async Task<ActionResult<Bot>> CreateBot(Bot bot)
    {
        bot.CreatedAt = DateTime.UtcNow;
        _context.Bots.Add(bot);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetBot), new { id = bot.Id }, bot);
    }

    // PUT: api/bots/5
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateBot(int id, Bot bot)
    {
        if (id != bot.Id) return BadRequest();

        bot.UpdatedAt = DateTime.UtcNow;
        _context.Entry(bot).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await BotExists(id)) return NotFound();
            throw;
        }

        return NoContent();
    }

    // DELETE: api/bots/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteBot(int id)
    {
        var bot = await _context.Bots.FindAsync(id);
        if (bot == null) return NotFound();

        // No se puede eliminar un bot que está corriendo
        if (bot.Status == "running")
            return BadRequest(new { message = "No se puede eliminar un bot que está en ejecución. Deténlo primero." });

        _context.Bots.Remove(bot);
        await _context.SaveChangesAsync();

        return Ok(new { message = $"Bot '{bot.Name}' eliminado exitosamente.", botId = id });
    }

    // POST: api/bots/5/run
    [HttpPost("{id}/run")]
    public async Task<IActionResult> RunBot(int id, [FromServices] IBackgroundJobClient backgroundJobClient)
    {
        var bot = await _context.Bots.FindAsync(id);

        if (bot == null)
            return NotFound(new { message = "Bot no encontrado." });

        if (!bot.IsActive)
            return BadRequest(new { message = "El bot no está activo." });

        if (bot.Status == "running")
            return BadRequest(new { message = "El bot ya está en ejecución." });

        backgroundJobClient.Enqueue<ScrapingJob>(job => job.ExecuteBotAsync(id));

        return Ok(new
        {
            message = "Bot encolado para ejecución exitosamente.",
            botId = id,
            botName = bot.Name
        });
    }

    // POST: api/bots/5/stop
    // Marca el bot para detención. El ScraperService comprueba este flag
    // en cada iteración y detiene el proceso limpiamente.
    [HttpPost("{id}/stop")]
    public async Task<IActionResult> StopBot(int id)
    {
        var bot = await _context.Bots.FindAsync(id);

        if (bot == null)
            return NotFound(new { message = "Bot no encontrado." });

        if (bot.Status != "running")
            return BadRequest(new { message = $"El bot no está en ejecución (estado actual: {bot.Status})." });

        // Marcar como "stopping" para que ScraperService lo detecte y detenga el loop
        bot.Status = "stopping";
        bot.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            message = $"Señal de detención enviada al bot '{bot.Name}'. Se detendrá al completar la iteración actual.",
            botId = id,
            botName = bot.Name
        });
    }

    // POST: api/bots/5/toggle  (activar / desactivar)
    [HttpPost("{id}/toggle")]
    public async Task<IActionResult> ToggleBot(int id)
    {
        var bot = await _context.Bots.FindAsync(id);
        if (bot == null) return NotFound();

        bot.IsActive = !bot.IsActive;
        bot.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            message = bot.IsActive ? $"Bot '{bot.Name}' activado." : $"Bot '{bot.Name}' desactivado.",
            botId = id,
            isActive = bot.IsActive
        });
    }

    private async Task<bool> BotExists(int id) =>
        await _context.Bots.AnyAsync(e => e.Id == id);
}