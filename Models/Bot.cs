namespace Inmobiscrap.Models;

public class Bot
{
    public int Id { get; set; }
    
    // Identificación
    public string Name { get; set; } = string.Empty; // "Scraper PortalInmobiliario Santiago"
    public string Source { get; set; } = string.Empty; // "portalinmobiliario"
    
    // Configuración
    public string Url { get; set; } = string.Empty; // URL que scrapea
    public bool IsActive { get; set; } = true; // Si el bot está activo o pausado
    
    // Estado de ejecución
    public DateTime? LastRun { get; set; } // Última vez que corrió
    public DateTime? NextRun { get; set; } // Próxima ejecución programada
    public string Status { get; set; } = "idle"; // "idle", "running", "error", "completed"
    
    // Estadísticas
    public int TotalScraped { get; set; } = 0; // Total de propiedades scrapeadas
    public int LastRunCount { get; set; } = 0; // Propiedades en última ejecución
    public string? LastError { get; set; } // Último error si falló
    
    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}