namespace Inmobiscrap.Models;

public class User
{
    public int Id { get; set; }

    // Credenciales
    public string Email { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }

    // Perfil
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }

    // OAuth Google
    public string? GoogleId { get; set; }

    // ── Capa comercial (qué pagas) ────────────────────────────────────────────
    /// <summary>"base" | "pro"</summary>
    public string Plan { get; set; } = "base";

    /// <summary>Créditos de consulta (solo relevante para plan base)</summary>
    public int Credits { get; set; } = 50;

    /// <summary>Fecha del último reset de créditos</summary>
    public DateTime? CreditsResetAt { get; set; }

    // ── Capa de autorización (qué puedes hacer) ───────────────────────────────
    /// <summary>"user" | "admin"</summary>
    public string Role { get; set; } = "user";

    // Control
    public bool IsActive { get; set; } = true;

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }

    // Navegación
    public ICollection<Bot> Bots { get; set; } = new List<Bot>();
}