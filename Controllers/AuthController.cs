using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using BCrypt.Net;
using Inmobiscrap.Data;
using Inmobiscrap.Models;

namespace Inmobiscrap.Controllers;

// ── DTOs ─────────────────────────────────────────────────────────

public record RegisterRequest(string Name, string Email, string Password);
public record LoginRequest(string Email, string Password);
public record GoogleAuthRequest(string IdToken);
public record AuthResponse(string Token, UserDto User);
public record UserDto(int Id, string Name, string Email, string? AvatarUrl, string Role, string Plan, int Credits);

// ── Controller ───────────────────────────────────────────────────

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ApplicationDbContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AuthController> logger)
    {
        _context = context;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Determina si un email debe recibir rol admin automáticamente.
    /// Configurado via SUPERADMIN_EMAIL en .env (puede ser CSV: "a@b.com,c@d.com")
    /// </summary>
    private bool IsSuperAdminEmail(string email)
    {
        var superAdminEmails = Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL") ?? "";
        return superAdminEmails
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(e => e.Equals(email, StringComparison.OrdinalIgnoreCase));
    }

    // ── POST /api/auth/register ───────────────────────────────────
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "Nombre, email y contraseña son requeridos." });

        if (req.Password.Length < 8)
            return BadRequest(new { message = "La contraseña debe tener al menos 8 caracteres." });

        var emailLower = req.Email.ToLower().Trim();

        if (await _context.Users.AnyAsync(u => u.Email == emailLower))
            return Conflict(new { message = "Ya existe una cuenta con ese email." });

        var isAdmin = IsSuperAdminEmail(emailLower);

        var user = new User
        {
            Name         = req.Name.Trim(),
            Email        = emailLower,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            Plan         = isAdmin ? "pro" : "base",
            Credits      = isAdmin ? 0 : 50,
            Role         = isAdmin ? "admin" : "user",
            CreatedAt    = DateTime.UtcNow,
            LastLogin    = DateTime.UtcNow,
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        if (isAdmin)
            _logger.LogInformation("🔑 SuperAdmin registered: {Email}", emailLower);

        return Ok(new AuthResponse(GenerateJwt(user), ToDto(user)));
    }

    // ── POST /api/auth/login ──────────────────────────────────────
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "Email y contraseña son requeridos." });

        var emailLower = req.Email.ToLower().Trim();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == emailLower);

        if (user == null || string.IsNullOrEmpty(user.PasswordHash))
            return Unauthorized(new { message = "Credenciales incorrectas." });

        if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { message = "Credenciales incorrectas." });

        if (!user.IsActive)
            return Unauthorized(new { message = "Tu cuenta está desactivada." });

        // Auto-promover si el email está en SUPERADMIN_EMAIL y aún no es admin
        if (IsSuperAdminEmail(emailLower) && user.Role != "admin")
        {
            user.Role = "admin";
            user.Plan = "pro";
            _logger.LogInformation("🔑 Existing user promoted to admin: {Email}", emailLower);
        }

        user.LastLogin = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new AuthResponse(GenerateJwt(user), ToDto(user)));
    }

    // ── POST /api/auth/google ─────────────────────────────────────
    [HttpPost("google")]
    public async Task<ActionResult<AuthResponse>> GoogleAuth([FromBody] GoogleAuthRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.IdToken))
            return BadRequest(new { message = "Token de Google no proporcionado." });

        GoogleTokenInfo? tokenInfo;
        try
        {
            tokenInfo = await ValidateGoogleTokenAsync(req.IdToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Google token validation failed: {Message}", ex.Message);
            return Unauthorized(new { message = "Token de Google inválido o expirado." });
        }

        if (tokenInfo == null)
            return Unauthorized(new { message = "No se pudo verificar el token de Google." });

        var expectedClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")
            ?? _configuration["Google:ClientId"];

        if (!string.IsNullOrEmpty(expectedClientId) && tokenInfo.Aud != expectedClientId)
            return Unauthorized(new { message = "Token de Google no pertenece a esta aplicación." });

        var emailLower = tokenInfo.Email?.ToLower().Trim() ?? "";
        var isAdmin = IsSuperAdminEmail(emailLower);

        var user = await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == tokenInfo.Sub)
                ?? await _context.Users.FirstOrDefaultAsync(u => u.Email == emailLower);

        if (user == null)
        {
            // Nuevo usuario vía Google
            user = new User
            {
                Name      = tokenInfo.Name ?? tokenInfo.Email ?? "Usuario",
                Email     = emailLower,
                GoogleId  = tokenInfo.Sub,
                AvatarUrl = tokenInfo.Picture,
                Plan      = isAdmin ? "pro" : "base",
                Credits   = isAdmin ? 0 : 50,
                Role      = isAdmin ? "admin" : "user",
                CreatedAt = DateTime.UtcNow,
                LastLogin = DateTime.UtcNow,
            };
            _context.Users.Add(user);
        }
        else
        {
            user.GoogleId  ??= tokenInfo.Sub;
            user.AvatarUrl ??= tokenInfo.Picture;
            user.LastLogin   = DateTime.UtcNow;

            // Auto-promover si está en SUPERADMIN_EMAIL
            if (isAdmin && user.Role != "admin")
            {
                user.Role = "admin";
                user.Plan = "pro";
            }
        }

        await _context.SaveChangesAsync();
        return Ok(new AuthResponse(GenerateJwt(user), ToDto(user)));
    }

    // ── GET /api/auth/me ──────────────────────────────────────────
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId.Value);
        if (user == null || !user.IsActive) return Unauthorized();

        return Ok(ToDto(user));
    }

    // ── GET /api/auth/credits ─────────────────────────────────────
    [HttpGet("credits")]
    [Authorize]
    public async Task<IActionResult> GetCredits()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId.Value);
        if (user == null) return Unauthorized();

        return Ok(new
        {
            plan = user.Plan,
            credits = user.Credits,
            unlimited = user.Plan == "pro",
            role = user.Role,
        });
    }

    // ── Helpers ───────────────────────────────────────────────────

    private string GenerateJwt(User user)
    {
        var jwtKey = Environment.GetEnvironmentVariable("JWT_SECRET")
            ?? _configuration["Jwt:Secret"]
            ?? throw new InvalidOperationException("JWT_SECRET not configured.");

        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresDays = int.TryParse(_configuration["Jwt:ExpireDays"], out var d) ? d : 7;

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("plan", user.Plan),
        };

        var token = new JwtSecurityToken(
            issuer:   _configuration["Jwt:Issuer"] ?? "inmobiscrap",
            audience: _configuration["Jwt:Audience"] ?? "inmobiscrap",
            claims:   claims,
            expires:  DateTime.UtcNow.AddDays(expiresDays),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<GoogleTokenInfo?> ValidateGoogleTokenAsync(string idToken)
    {
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync(
            $"https://oauth2.googleapis.com/tokeninfo?id_token={idToken}");

        if (!response.IsSuccessStatusCode)
            throw new Exception("Google rejected the token.");

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<GoogleTokenInfo>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private int? GetCurrentUserId()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(idClaim, out var id) ? id : null;
    }

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Name, u.Email, u.AvatarUrl, u.Role, u.Plan, u.Credits);

    private class GoogleTokenInfo
    {
        public string? Sub     { get; set; }
        public string? Email   { get; set; }
        public string? Name    { get; set; }
        public string? Picture { get; set; }
        public string? Aud     { get; set; }
    }
}