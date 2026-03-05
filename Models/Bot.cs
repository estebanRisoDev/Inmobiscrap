namespace Inmobiscrap.Models;

public class Bot
{
    public int Id { get; set; }

    // ── Relación con usuario ──────────────────────────────────────────────────
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    // Identificación
    public string Name   { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    // Configuración
    public string Url      { get; set; } = string.Empty;
    public bool   IsActive { get; set; } = true;

    // Programación
    public bool    ScheduleEnabled { get; set; } = false;
    public string? CronExpression  { get; set; }

    // Estado de ejecución
    public DateTime? LastRun  { get; set; }
    public DateTime? NextRun  { get; set; }
    public string    Status   { get; set; } = "idle";

    // Estadísticas
    public int     TotalScraped  { get; set; } = 0;
    public int     LastRunCount  { get; set; } = 0;
    public string? LastError     { get; set; }

    // Timestamps
    public DateTime  CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}