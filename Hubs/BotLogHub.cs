using Microsoft.AspNetCore.SignalR;

namespace Inmobiscrap.Hubs;

/// <summary>
/// Hub de SignalR para transmitir logs de bots en tiempo real
/// </summary>
public class BotLogHub : Hub
{
    private readonly ILogger<BotLogHub> _logger;

    public BotLogHub(ILogger<BotLogHub> logger)
    {
        _logger = logger;
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
    /// Suscribe al cliente a un canal específico.
    /// Si botId tiene valor -> Canal del Bot específico.
    /// Si botId es null -> Canal Global (Dashboard).
    /// </summary>
    public async Task SubscribeToBot(int? botId)
    {
        string groupName;
        string message;

        if (botId.HasValue && botId.Value > 0)
        {
            // Suscripción a un bot específico
            groupName = $"Bot_{botId}";
            message = $"Suscrito a logs del Bot #{botId}";
        }
        else
        {
            // Suscripción global (Dashboard general)
            groupName = "Dashboard_Global";
            message = "Suscrito al Dashboard Global (Todos los bots)";
        }

        // Añadir al grupo correspondiente
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        
        // Confirmar suscripción al cliente
        await Clients.Caller.SendAsync("ReceiveLogMessage", new
        {
            Level = "Info",
            Message = message,
            Timestamp = DateTime.UtcNow,
            BotId = botId,
            BotName = "System"
        });
        
        _logger.LogInformation($"Client {Context.ConnectionId} joined group {groupName}");
    }

    /// <summary>
    /// Desuscribirse (Opcional, útil si cambias de bot sin desconectar)
    /// </summary>
    public async Task UnsubscribeFromBot(int? botId)
    {
        string groupName = (botId.HasValue && botId.Value > 0) 
            ? $"Bot_{botId}" 
            : "Dashboard_Global";

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }
}