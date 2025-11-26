using Microsoft.AspNetCore.SignalR;
using Inmobiscrap.Hubs;

namespace Inmobiscrap.Services;

public interface IBotLogService
{
    Task LogInfoAsync(int botId, string botName, string message);
    Task LogWarningAsync(int botId, string botName, string message);
    Task LogErrorAsync(int botId, string botName, string message, Exception? exception = null);
    Task LogDebugAsync(int botId, string botName, string message);
    Task LogSuccessAsync(int botId, string botName, string message);
    Task SendProgressAsync(int botId, string botName, int current, int total, string? message = null);
}

public class BotLogService : IBotLogService
{
    private readonly IHubContext<BotLogHub> _hubContext;
    private readonly ILogger<BotLogService> _logger;

    public BotLogService(
        IHubContext<BotLogHub> hubContext,
        ILogger<BotLogService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task LogInfoAsync(int botId, string botName, string message) => 
        await SendLogAsync(botId, botName, "Info", message);

    public async Task LogWarningAsync(int botId, string botName, string message) => 
        await SendLogAsync(botId, botName, "Warning", message);

    public async Task LogErrorAsync(int botId, string botName, string message, Exception? exception = null)
    {
        var fullMessage = exception != null 
            ? $"{message} - Exception: {exception.Message}" 
            : message;
        await SendLogAsync(botId, botName, "Error", fullMessage);
    }

    public async Task LogDebugAsync(int botId, string botName, string message) => 
        await SendLogAsync(botId, botName, "Debug", message);

    public async Task LogSuccessAsync(int botId, string botName, string message) => 
        await SendLogAsync(botId, botName, "Success", message);

    public async Task SendProgressAsync(int botId, string botName, int current, int total, string? message = null)
    {
        var progressMessage = message ?? $"Progress: {current}/{total}";
        var percentage = total > 0 ? (current * 100) / total : 0;
        
        var payload = new
        {
            BotId = botId,
            BotName = botName,
            Current = current,
            Total = total,
            Percentage = percentage,
            Message = progressMessage,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            // 1. Enviar al grupo ESPECÍFICO del bot (quien mira el detalle)
            await _hubContext.Clients.Group($"Bot_{botId}").SendAsync("ReceiveProgress", payload);

            // 2. Enviar al grupo GLOBAL (quien mira el dashboard general)
            await _hubContext.Clients.Group("Dashboard_Global").SendAsync("ReceiveProgress", payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending progress update via SignalR");
        }
    }

    private async Task SendLogAsync(int botId, string botName, string level, string message)
    {
        try
        {
            var logEntry = new
            {
                Level = level,
                Message = message,
                Timestamp = DateTime.UtcNow,
                BotId = botId,
                BotName = botName
            };

            // 1. Enviar al grupo ESPECÍFICO
            await _hubContext.Clients.Group($"Bot_{botId}").SendAsync("ReceiveLogMessage", logEntry);

            // 2. Enviar al grupo GLOBAL
            await _hubContext.Clients.Group("Dashboard_Global").SendAsync("ReceiveLogMessage", logEntry);

            // Log interno del servidor
            _logger.LogInformation($"[Bot {botId}] [{level}] {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error sending log via SignalR for bot {botId}");
        }
    }
}