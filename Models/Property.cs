namespace Inmobiscrap.Models;

public class Property
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;
    // Detalles de la propiedad
    public decimal? Price { get; set; }
    public string? Currency { get; set; } = "CLP"; // Por si hay propiedades en USD
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Neighborhood { get; set; }
    
    // Características
    public int? Bedrooms { get; set; }
    public int? Bathrooms { get; set; }
    public decimal? Area { get; set; } // en m²
    public string? PropertyType { get; set; } // "departamento", "casa", "oficina"
    
    // Descripción y análisis
    public string? Description { get; set; }
}