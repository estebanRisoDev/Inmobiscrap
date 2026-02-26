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

    /// <summary>
    /// Limpia el buffer de logs de un bot. Llamar al inicio de cada nueva ejecución.
    /// </summary>
    void ClearBotLogs(int botId);
}

public class BotLogService : IBotLogService
{
    private readonly IHubContext<BotLogHub> _hubContext;
    private readonly ILogger<BotLogService> _logger;
    private readonly IBotLogBuffer _logBuffer;

    public BotLogService(
        IHubContext<BotLogHub> hubContext,
        ILogger<BotLogService> logger,
        IBotLogBuffer logBuffer)
    {
        _hubContext = hubContext;
        _logger = logger;
        _logBuffer = logBuffer;
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

        // Guardar en buffer (último estado de progreso del bot)
        _logBuffer.SetProgress(new BotProgressEntry(
            BotId: botId,
            BotName: botName,
            Current: current,
            Total: total,
            Percentage: percentage,
            Message: progressMessage,
            Timestamp: DateTime.UtcNow));

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

    public void ClearBotLogs(int botId)
    {
        _logBuffer.Clear(botId);
    }

    private async Task SendLogAsync(int botId, string botName, string level, string message)
    {
        try
        {
            var entry = new BotLogEntry(
                Level: level,
                Message: message,
                Timestamp: DateTime.UtcNow,
                BotId: botId,
                BotName: botName);

            // Guardar en buffer para clientes que se conecten tarde o reconecten
            _logBuffer.AddLog(entry);

            // 1. Enviar al grupo ESPECÍFICO
            await _hubContext.Clients.Group($"Bot_{botId}").SendAsync("ReceiveLogMessage", entry);

            // 2. Enviar al grupo GLOBAL
            await _hubContext.Clients.Group("Dashboard_Global").SendAsync("ReceiveLogMessage", entry);

            // Log interno del servidor
            _logger.LogInformation($"[Bot {botId}] [{level}] {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error sending log via SignalR for bot {botId}");
        }
    }
}