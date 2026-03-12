namespace Inmobiscrap.Models;

/// <summary>
/// Foto instantánea de una propiedad en el momento que un bot la encontró.
/// Se crea UNA por cada observación (cada scrape que detecta la propiedad).
///
/// Ejemplo: si un bot scrapea todos los días a las 10 AM y encuentra la misma
/// propiedad 30 veces en un mes, se crean 30 snapshots.
/// Esto permite:
///   - Graficar precio vs tiempo
///   - Ver cuántos días estuvo listada
///   - Detectar cuándo cambió algo
///   - Detectar cuándo dejó de aparecer (posible venta)
///   - Filtrar historial por ubicación sin necesitar JOIN con Property
/// </summary>
public class PropertySnapshot
{
    public long Id { get; set; }  // long porque puede haber muchos

    // ── Relación ──────────────────────────────────────────────────────────────
    public int PropertyId { get; set; }
    public Property Property { get; set; } = null!;

    // ── Cuándo y quién ────────────────────────────────────────────────────────
    /// <summary>Momento exacto del scrape</summary>
    public DateTime ScrapedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Bot que lo detectó</summary>
    public int BotId { get; set; }

    // ── Foto de los campos clave en ese momento ───────────────────────────────
    public decimal? Price { get; set; }
    public string?  Currency { get; set; }
    public int?     Bedrooms { get; set; }
    public int?     Bathrooms { get; set; }
    public decimal? Area { get; set; }
    public string?  PropertyType { get; set; }
    public DateTime? PublicationDate { get; set; }
    public string?  Condition { get; set; }
    public string?  Title { get; set; }

    // ── Ubicación (desnormalizada desde Property para consultas rápidas) ──────
    /// <summary>Región al momento del scrape</summary>
    public string? Region { get; set; }

    /// <summary>Ciudad al momento del scrape</summary>
    public string? City { get; set; }

    /// <summary>Comuna / barrio al momento del scrape</summary>
    public string? Neighborhood { get; set; }

    // ── Metadata ──────────────────────────────────────────────────────────────
    /// <summary>¿Cambió algo respecto al snapshot anterior?</summary>
    public bool HasChanges { get; set; } = false;

    /// <summary>Campos que cambiaron, separados por coma: "Price,Area"</summary>
    public string? ChangedFields { get; set; }
}