using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Inmobiscrap.Data;
using Inmobiscrap.Models;
using Inmobiscrap.Jobs;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/[controller]")]
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

        if (bot == null)
            return NotFound();

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
        if (id != bot.Id)
            return BadRequest();

        bot.UpdatedAt = DateTime.UtcNow;
        _context.Entry(bot).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await BotExists(id))
                return NotFound();
            throw;
        }

        return NoContent();
    }

    // DELETE: api/bots/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteBot(int id)
    {
        var bot = await _context.Bots.FindAsync(id);
        if (bot == null)
            return NotFound();

        _context.Bots.Remove(bot);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    // POST: api/bots/5/run
    [HttpPost("{id}/run")]
    public async Task<IActionResult> RunBot(int id, [FromServices] IBackgroundJobClient backgroundJobClient)
    {
        var bot = await _context.Bots.FindAsync(id);
        
        if (bot == null)
            return NotFound(new { message = "Bot not found" });
        
        if (!bot.IsActive)
            return BadRequest(new { message = "Bot is not active" });

        // Encolar el job en Hangfire
        backgroundJobClient.Enqueue<ScrapingJob>(job => job.ExecuteBotAsync(id));
        
        return Ok(new { 
            message = "Bot execution queued successfully",
            botId = id,
            botName = bot.Name
        });
    }

    private async Task<bool> BotExists(int id)
    {
        return await _context.Bots.AnyAsync(e => e.Id == id);
    }
}