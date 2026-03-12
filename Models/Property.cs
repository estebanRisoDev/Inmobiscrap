namespace Inmobiscrap.Models;

public class Property
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    // Detalles
    public decimal? Price { get; set; }
    public string? Currency { get; set; } = "CLP";
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Neighborhood { get; set; }
    public string? SourceUrl { get; set; }

    // Características
    public int? Bedrooms { get; set; }
    public int? Bathrooms { get; set; }
    public decimal? Area { get; set; }
    public string? PropertyType { get; set; }

    /// <summary>Fecha de publicación del aviso (extraída del sitio web)</summary>
    public DateTime? PublicationDate { get; set; }

    /// <summary>"Nuevo" | "Usado" | null</summary>
    public string? Condition { get; set; }

    // Descripción
    public string? Description { get; set; }

    // ── Tracking ──────────────────────────────────────────────────────────────

    /// <summary>SHA256 de SourceUrl normalizada (identifica la misma propiedad entre scrapes)</summary>
    public string? Fingerprint { get; set; }

    /// <summary>Primera vez que un bot encontró esta propiedad</summary>
    public DateTime? FirstSeenAt { get; set; }

    /// <summary>Última vez que un bot la encontró</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Cuántas veces ha sido observada por bots</summary>
    public int TimesScraped { get; set; } = 1;

    /// <summary>"active" | "price_changed" | "delisted"</summary>
    public string? ListingStatus { get; set; } = "active";

    /// <summary>Precio de la observación anterior (atajo para mostrar variación)</summary>
    public decimal? PreviousPrice { get; set; }

    /// <summary>Cuándo cambió de precio por última vez</summary>
    public DateTime? PriceChangedAt { get; set; }

    // ── Navegación ────────────────────────────────────────────────────────────
    public ICollection<PropertySnapshot> Snapshots { get; set; } = new List<PropertySnapshot>();
}