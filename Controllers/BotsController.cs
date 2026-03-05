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
    private readonly ApplicationDbContext  _context;
    private readonly IRecurringJobManager  _recurringJobs;

    // ID del job de Hangfire por bot: "bot-schedule-{id}"
    private static string JobId(int botId) => $"bot-schedule-{botId}";

    public BotsController(ApplicationDbContext context, IRecurringJobManager recurringJobs)
    {
        _context       = context;
        _recurringJobs = recurringJobs;
    }

    // GET: api/bots
    [HttpGet]
    public async Task<ActionResult<List<Bot>>> GetBots()
        => await _context.Bots.ToListAsync();

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

        // Registrar job de Hangfire si viene con schedule activado
        SyncHangfireJob(bot);

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

        // Actualizar o eliminar job de Hangfire según nuevo estado
        SyncHangfireJob(bot);

        return NoContent();
    }

    // DELETE: api/bots/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteBot(int id)
    {
        var bot = await _context.Bots.FindAsync(id);
        if (bot == null) return NotFound();

        if (bot.Status == "running")
            return BadRequest(new { message = "No se puede eliminar un bot que está en ejecución." });

        // Eliminar job de Hangfire asociado
        _recurringJobs.RemoveIfExists(JobId(id));

        _context.Bots.Remove(bot);
        await _context.SaveChangesAsync();

        return Ok(new { message = $"Bot '{bot.Name}' eliminado.", botId = id });
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

        return Ok(new { message = "Bot encolado.", botId = id, botName = bot.Name });
    }

    // POST: api/bots/5/stop
    [HttpPost("{id}/stop")]
    public async Task<IActionResult> StopBot(int id)
    {
        var bot = await _context.Bots.FindAsync(id);

        if (bot == null)
            return NotFound(new { message = "Bot no encontrado." });

        if (bot.Status != "running")
            return BadRequest(new { message = $"El bot no está en ejecución (estado: {bot.Status})." });

        bot.Status    = "stopping";
        bot.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { message = $"Señal de detención enviada al bot '{bot.Name}'.", botId = id });
    }

    // POST: api/bots/5/toggle
    [HttpPost("{id}/toggle")]
    public async Task<IActionResult> ToggleBot(int id)
    {
        var bot = await _context.Bots.FindAsync(id);
        if (bot == null) return NotFound();

        bot.IsActive  = !bot.IsActive;
        bot.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Si se desactiva el bot, suspender también el job programado
        SyncHangfireJob(bot);

        return Ok(new { message = bot.IsActive ? $"Bot '{bot.Name}' activado." : $"Bot '{bot.Name}' desactivado.", isActive = bot.IsActive });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sincroniza el job de Hangfire con el estado actual del bot:
    /// - Si ScheduleEnabled=true, IsActive=true y CronExpression válida → registra/actualiza job
    /// - En cualquier otro caso → elimina el job
    /// </summary>
    private void SyncHangfireJob(Bot bot)
    {
        var jobId = JobId(bot.Id);

        if (bot.IsActive && bot.ScheduleEnabled && !string.IsNullOrWhiteSpace(bot.CronExpression))
        {
            _recurringJobs.AddOrUpdate<ScrapingJob>(
                jobId,
                job => job.ExecuteBotAsync(bot.Id),
                bot.CronExpression);
        }
        else
        {
            _recurringJobs.RemoveIfExists(jobId);
        }
    }

    private async Task<bool> BotExists(int id)
        => await _context.Bots.AnyAsync(e => e.Id == id);
}