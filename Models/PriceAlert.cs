namespace Inmobiscrap.Models;

/// <summary>
/// Suscripción de un usuario Pro a alertas de bajada de precio de una propiedad.
/// Cuando el job diario detecta que Price bajó respecto a PreviousPrice,
/// envía un email al usuario y actualiza LastNotifiedAt.
/// </summary>
public class PriceAlert
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int PropertyId { get; set; }
    public Property Property { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Última vez que se envió notificación por esta alerta. Evita duplicados.</summary>
    public DateTime? LastNotifiedAt { get; set; }
}
