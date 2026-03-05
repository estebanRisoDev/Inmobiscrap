using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Inmobiscrap.Data;
using Inmobiscrap.Services;

namespace Inmobiscrap.Jobs;

public class ScrapingJob
{
    private readonly ApplicationDbContext _context;
    private readonly IScraperService _scraperService;
    private readonly ILogger<ScrapingJob> _logger;

    // ── Únicos estados que permiten ejecución automática ─────────────────────
    // "completed", "running", "stopping" quedan bloqueados intencionalmente.
    // Un bot completado solo se puede volver a ejecutar si el usuario lo resetea
    // manualmente a "idle" desde la UI.
    private static readonly string[] _runnableStatuses = { "idle", "error" };

    public ScrapingJob(
        ApplicationDbContext context,
        IScraperService scraperService,
        ILogger<ScrapingJob> logger)
    {
        _context = context;
        _scraperService = scraperService;
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta un bot específico por su ID.
    /// Llamado manualmente desde BotsController.
    /// </summary>
    public async Task ExecuteBotAsync(int botId)
    {
        _logger.LogInformation("Starting execution of bot {BotId}", botId);

        var bot = await _context.Bots.FindAsync(botId);

        if (bot == null)
        {
            _logger.LogWarning("Bot {BotId} not found", botId);
            return;
        }

        if (!bot.IsActive)
        {
            _logger.LogWarning("Bot '{BotName}' (ID: {BotId}) is not active", bot.Name, botId);
            return;
        }

        try
        {
            await _scraperService.ScrapePropertiesAsync(bot);
            _logger.LogInformation("Bot '{BotName}' (ID: {BotId}) executed successfully", bot.Name, botId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing bot '{BotName}' (ID: {BotId})", bot.Name, botId);
        }
    }

    /// <summary>
    /// Ejecuta todos los bots elegibles (solo idle o error).
    /// Bots en completed, running o stopping son ignorados completamente.
    /// Disparado por Hangfire cada hora.
    /// </summary>
    public async Task ExecuteAllActiveBotsAsync()
    {
        _logger.LogInformation("Starting scheduled execution of all eligible bots");

        var eligibleBots = await _context.Bots
            .Where(b => b.IsActive && _runnableStatuses.Contains(b.Status))
            .ToListAsync();

        if (!eligibleBots.Any())
        {
            _logger.LogInformation("No eligible bots to run (all are completed, running or stopped)");
            return;
        }

        _logger.LogInformation("Found {Count} eligible bot(s) to run", eligibleBots.Count);

        foreach (var bot in eligibleBots)
        {
            try
            {
                _logger.LogInformation("Executing bot '{BotName}' (ID: {BotId})", bot.Name, bot.Id);
                await _scraperService.ScrapePropertiesAsync(bot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing bot '{BotName}' (ID: {BotId})", bot.Name, bot.Id);
                // Continuar con el siguiente bot aunque este falle
            }
        }

        _logger.LogInformation("Finished scheduled execution");
    }
}