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
    /// Ejecuta un bot específico por su ID
    /// </summary>
    public async Task ExecuteBotAsync(int botId)
    {
        _logger.LogInformation($"Starting execution of bot {botId}");
        
        var bot = await _context.Bots.FindAsync(botId);
        
        if (bot == null)
        {
            _logger.LogWarning($"Bot {botId} not found");
            return;
        }

        if (!bot.IsActive)
        {
            _logger.LogWarning($"Bot '{bot.Name}' (ID: {botId}) is not active");
            return;
        }

        try
        {
            await _scraperService.ScrapePropertiesAsync(bot);
            _logger.LogInformation($"Bot '{bot.Name}' (ID: {botId}) executed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error executing bot '{bot.Name}' (ID: {botId})");
        }
    }

    /// <summary>
    /// Ejecuta todos los bots activos
    /// </summary>
    public async Task ExecuteAllActiveBotsAsync()
    {
        _logger.LogInformation("Starting execution of all active bots");
        
        var activeBots = await _context.Bots
            .Where(b => b.IsActive)
            .ToListAsync();

        _logger.LogInformation($"Found {activeBots.Count} active bots");

        foreach (var bot in activeBots)
        {
            try
            {
                _logger.LogInformation($"Executing bot '{bot.Name}' (ID: {bot.Id})");
                await _scraperService.ScrapePropertiesAsync(bot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing bot '{bot.Name}' (ID: {bot.Id})");
                // Continuar con el siguiente bot aunque este falle
            }
        }

        _logger.LogInformation("Finished execution of all active bots");
    }
}