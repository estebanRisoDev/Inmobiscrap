namespace Inmobiscrap.Models;

public class User
{
    public int Id { get; set; }

    // Credenciales
    public string Email { get; set; } = string.Empty;
    public string? PasswordHash { get; set; } // null para usuarios solo-Google

    // Perfil
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }

    // OAuth Google
    public string? GoogleId { get; set; }

    // Control
    public bool IsActive { get; set; } = true;
    public string Role { get; set; } = "user"; // "user" | "admin"

    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }
}