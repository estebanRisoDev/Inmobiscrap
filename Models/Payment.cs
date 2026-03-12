namespace Inmobiscrap.Models;

public class Payment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>"pro_activated" | "pro_renewed" | "pro_cancelled" | "pack50" | "pack100" | "pack1000"</summary>
    public string Type { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Monto cobrado en CLP (0 para eventos como cancelación)</summary>
    public int AmountCLP { get; set; }

    /// <summary>Créditos acreditados (0 para Pro o cancelación)</summary>
    public int Credits { get; set; }

    /// <summary>ID del pago o preapproval en MercadoPago</summary>
    public string? MpId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
