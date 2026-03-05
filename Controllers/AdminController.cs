using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;

namespace Inmobiscrap.Controllers;

// ── DTOs ─────────────────────────────────────────────────────────

public record UserListItem(
    int Id, string Name, string Email, string? AvatarUrl,
    string Role, string Plan, int Credits, bool IsActive,
    DateTime CreatedAt, DateTime? LastLogin, int BotCount);

public record UpdateUserRequest(string? Role, string? Plan, int? Credits, bool? IsActive);

// ── Controller ───────────────────────────────────────────────────

[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "admin")]
public class AdminController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<AdminController> _logger;

    public AdminController(ApplicationDbContext context, ILogger<AdminController> logger)
    {
        _context = context;
        _logger = logger;
    }

    private int GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : 0;
    }

    // ── GET /api/admin/users ──────────────────────────────────────
    /// <summary>Lista todos los usuarios con conteo de bots.</summary>
    [HttpGet]
    public async Task<ActionResult<List<UserListItem>>> GetUsers(
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        [FromQuery] string? plan = null)
    {
        var query = _context.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower().Trim();
            query = query.Where(u =>
                u.Name.ToLower().Contains(s) ||
                u.Email.ToLower().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(u => u.Role == role);

        if (!string.IsNullOrWhiteSpace(plan))
            query = query.Where(u => u.Plan == plan);

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new UserListItem(
                u.Id, u.Name, u.Email, u.AvatarUrl,
                u.Role, u.Plan, u.Credits, u.IsActive,
                u.CreatedAt, u.LastLogin,
                u.Bots.Count))
            .ToListAsync();

        return Ok(users);
    }

    // ── GET /api/admin/users/{id} ─────────────────────────────────
    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(int id)
    {
        var user = await _context.Users
            .Include(u => u.Bots)
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return NotFound();

        return Ok(new
        {
            user.Id, user.Name, user.Email, user.AvatarUrl,
            user.Role, user.Plan, user.Credits, user.IsActive,
            user.CreatedAt, user.LastLogin,
            bots = user.Bots.Select(b => new
            {
                b.Id, b.Name, b.Source, b.Status, b.TotalScraped, b.IsActive
            }),
        });
    }

    // ── PATCH /api/admin/users/{id} ───────────────────────────────
    /// <summary>Actualiza rol, plan, créditos o estado activo de un usuario.</summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var currentUserId = GetCurrentUserId();
        var user = await _context.Users.FindAsync(id);

        if (user == null) return NotFound(new { message = "Usuario no encontrado." });

        // Protección: no puedes quitarte admin a ti mismo
        if (id == currentUserId && req.Role != null && req.Role != "admin")
            return BadRequest(new { message = "No puedes quitarte el rol de admin a ti mismo." });

        // Protección: no puedes desactivarte a ti mismo
        if (id == currentUserId && req.IsActive == false)
            return BadRequest(new { message = "No puedes desactivar tu propia cuenta." });

        // Validar valores
        if (req.Role != null && req.Role is not ("user" or "admin"))
            return BadRequest(new { message = "Rol inválido. Valores: user, admin." });

        if (req.Plan != null && req.Plan is not ("base" or "pro"))
            return BadRequest(new { message = "Plan inválido. Valores: base, pro." });

        if (req.Credits.HasValue && req.Credits.Value < 0)
            return BadRequest(new { message = "Los créditos no pueden ser negativos." });

        // Aplicar cambios
        var changes = new List<string>();

        if (req.Role != null && req.Role != user.Role)
        {
            changes.Add($"role: {user.Role} → {req.Role}");
            user.Role = req.Role;
        }

        if (req.Plan != null && req.Plan != user.Plan)
        {
            changes.Add($"plan: {user.Plan} → {req.Plan}");
            user.Plan = req.Plan;
        }

        if (req.Credits.HasValue && req.Credits.Value != user.Credits)
        {
            changes.Add($"credits: {user.Credits} → {req.Credits.Value}");
            user.Credits = req.Credits.Value;
        }

        if (req.IsActive.HasValue && req.IsActive.Value != user.IsActive)
        {
            changes.Add($"active: {user.IsActive} → {req.IsActive.Value}");
            user.IsActive = req.IsActive.Value;
        }

        if (changes.Count == 0)
            return Ok(new { message = "Sin cambios.", user = ToListItem(user) });

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Admin {AdminId} updated user {UserId} ({Email}): {Changes}",
            currentUserId, id, user.Email, string.Join(", ", changes));

        return Ok(new
        {
            message = $"Usuario actualizado: {string.Join(", ", changes)}",
            user = ToListItem(user),
        });
    }

    // ── GET /api/admin/users/stats ────────────────────────────────
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var total  = await _context.Users.CountAsync();
        var active = await _context.Users.CountAsync(u => u.IsActive);
        var admins = await _context.Users.CountAsync(u => u.Role == "admin");
        var pro    = await _context.Users.CountAsync(u => u.Plan == "pro");
        var basePlan = await _context.Users.CountAsync(u => u.Plan == "base");

        return Ok(new { total, active, inactive = total - active, admins, pro, basePlan });
    }

    // ── Helper ───────────────────────────────────────────────────

    private static UserListItem ToListItem(Inmobiscrap.Models.User u) =>
        new(u.Id, u.Name, u.Email, u.AvatarUrl, u.Role, u.Plan, u.Credits, u.IsActive, u.CreatedAt, u.LastLogin, 0);
}