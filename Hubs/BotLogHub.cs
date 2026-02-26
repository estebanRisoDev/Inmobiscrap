using Microsoft.AspNetCore.SignalR;
using Inmobiscrap.Services;

namespace Inmobiscrap.Hubs;

/// <summary>
/// Hub de SignalR para transmitir logs de bots en tiempo real.
/// Al suscribirse, envía el historial en buffer antes de conectar al stream en vivo.
/// </summary>
public class BotLogHub : Hub
{
    private readonly ILogger<BotLogHub> _logger;
    private readonly IBotLogBuffer _logBuffer;

    public BotLogHub(ILogger<BotLogHub> logger, IBotLogBuffer logBuffer)
    {
        _logger = logger;
        _logBuffer = logBuffer;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation($"Client connected: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation($"Client disconnected: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Suscribe al cliente a un canal específico y envía el historial en buffer.
    /// Si botId tiene valor -> Canal del Bot específico.
    /// Si botId es null -> Canal Global (Dashboard).
    /// </summary>
    public async Task SubscribeToBot(int? botId)
    {
        string groupName;

        if (botId.HasValue && botId.Value > 0)
        {
            groupName = $"Bot_{botId}";

            // Recuperar historial del bot y enviarlo de una sola vez (ReceiveHistory)
            var history = _logBuffer.GetLogs(botId.Value);
            if (history.Count > 0)
            {
                await Clients.Caller.SendAsync("ReceiveHistory", history);
            }

            // Enviar último estado de progreso si existe
            var lastProgress = _logBuffer.GetProgress(botId.Value);
            if (lastProgress != null)
            {
                await Clients.Caller.SendAsync("ReceiveProgress", lastProgress);
            }
        }
        else
        {
            groupName = "Dashboard_Global";

            // Para el dashboard global: historial reciente de todos los bots
            var history = _logBuffer.GetAllRecentLogs(maxPerBot: 100);
            if (history.Count > 0)
            {
                await Clients.Caller.SendAsync("ReceiveHistory", history);
            }
        }

        // Añadir al grupo DESPUÉS de enviar historial (para recibir eventos en vivo)
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        _logger.LogInformation($"Client {Context.ConnectionId} joined group {groupName}");
    }

    /// <summary>
    /// Desuscribirse (útil si cambias de bot sin desconectar)
    /// </summary>
    public async Task UnsubscribeFromBot(int? botId)
    {
        string groupName = (botId.HasValue && botId.Value > 0)
            ? $"Bot_{botId}"
            : "Dashboard_Global";

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }
}