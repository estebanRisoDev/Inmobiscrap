namespace Inmobiscrap.Services;

/// <summary>
/// Entrada de log almacenada en el buffer de memoria.
/// Las propiedades en PascalCase serán enviadas como camelCase por SignalR.
/// </summary>
public record BotLogEntry(
    string Level,
    string Message,
    DateTime Timestamp,
    int BotId,
    string BotName);

/// <summary>
/// Entrada de progreso almacenada en el buffer de memoria.
/// </summary>
public record BotProgressEntry(
    int BotId,
    string BotName,
    int Current,
    int Total,
    int Percentage,
    string Message,
    DateTime Timestamp);

public interface IBotLogBuffer
{
    /// <summary>Agrega un log al buffer del bot.</summary>
    void AddLog(BotLogEntry entry);

    /// <summary>Actualiza el último estado de progreso del bot.</summary>
    void SetProgress(BotProgressEntry entry);

    /// <summary>Retorna todos los logs en buffer para un bot específico.</summary>
    IReadOnlyList<BotLogEntry> GetLogs(int botId);

    /// <summary>Retorna el último progreso conocido del bot, o null si no hay.</summary>
    BotProgressEntry? GetProgress(int botId);

    /// <summary>
    /// Retorna los logs más recientes de TODOS los bots (para el dashboard global).
    /// </summary>
    IReadOnlyList<BotLogEntry> GetAllRecentLogs(int maxPerBot = 100);

    /// <summary>
    /// Limpia el historial de un bot específico.
    /// Llamar al inicio de cada nueva ejecución de scraping.
    /// </summary>
    void Clear(int botId);
}

/// <summary>
/// Buffer en memoria thread-safe que guarda los últimos N logs por bot.
/// Registrado como Singleton para persistir entre requests y conexiones.
/// </summary>
public class BotLogBuffer : IBotLogBuffer
{
    private const int MaxLogsPerBot = 1000;

    private readonly Dictionary<int, List<BotLogEntry>> _logs = new();
    private readonly Dictionary<int, BotProgressEntry?> _progress = new();
    private readonly object _lock = new();

    public void AddLog(BotLogEntry entry)
    {
        lock (_lock)
        {
            if (!_logs.TryGetValue(entry.BotId, out var list))
            {
                list = new List<BotLogEntry>(MaxLogsPerBot);
                _logs[entry.BotId] = list;
            }

            list.Add(entry);

            // Ventana deslizante: eliminar el más antiguo si se supera el límite
            if (list.Count > MaxLogsPerBot)
                list.RemoveAt(0);
        }
    }

    public void SetProgress(BotProgressEntry entry)
    {
        lock (_lock)
        {
            _progress[entry.BotId] = entry;
        }
    }

    public IReadOnlyList<BotLogEntry> GetLogs(int botId)
    {
        lock (_lock)
        {
            return _logs.TryGetValue(botId, out var list)
                ? list.ToList()
                : Array.Empty<BotLogEntry>();
        }
    }

    public BotProgressEntry? GetProgress(int botId)
    {
        lock (_lock)
        {
            return _progress.TryGetValue(botId, out var p) ? p : null;
        }
    }

    public IReadOnlyList<BotLogEntry> GetAllRecentLogs(int maxPerBot = 100)
    {
        lock (_lock)
        {
            return _logs.Values
                .SelectMany(list => list.TakeLast(maxPerBot))
                .OrderBy(e => e.Timestamp)
                .ToList();
        }
    }

    public void Clear(int botId)
    {
        lock (_lock)
        {
            _logs[botId] = new List<BotLogEntry>(MaxLogsPerBot);
            _progress.Remove(botId);
        }
    }
}
