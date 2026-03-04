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
public record UserDto(int Id, string Name, string Email, string? AvatarUrl, string Role);

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

        var user = new User
        {
            Name         = req.Name.Trim(),
            Email        = emailLower,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            CreatedAt    = DateTime.UtcNow,
            LastLogin    = DateTime.UtcNow,
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var token = GenerateJwt(user);
        return Ok(new AuthResponse(token, ToDto(user)));
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

        user.LastLogin = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var token = GenerateJwt(user);
        return Ok(new AuthResponse(token, ToDto(user)));
    }

    // ── POST /api/auth/google ─────────────────────────────────────
    // Recibe el id_token de Google (credential del botón Google Identity)
    // y lo valida consultando la endpoint de tokeninfo de Google.
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

        // Verificar que el audience coincide con tu Google Client ID
        var expectedClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")
            ?? _configuration["Google:ClientId"];

        if (!string.IsNullOrEmpty(expectedClientId) && tokenInfo.Aud != expectedClientId)
            return Unauthorized(new { message = "Token de Google no pertenece a esta aplicación." });

        var emailLower = tokenInfo.Email?.ToLower().Trim() ?? "";

        // Buscar por GoogleId primero, luego por email
        var user = await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == tokenInfo.Sub)
                ?? await _context.Users.FirstOrDefaultAsync(u => u.Email == emailLower);

        if (user == null)
        {
            // Crear nuevo usuario via Google
            user = new User
            {
                Name      = tokenInfo.Name ?? tokenInfo.Email ?? "Usuario",
                Email     = emailLower,
                GoogleId  = tokenInfo.Sub,
                AvatarUrl = tokenInfo.Picture,
                CreatedAt = DateTime.UtcNow,
                LastLogin = DateTime.UtcNow,
            };
            _context.Users.Add(user);
        }
        else
        {
            // Actualizar perfil de Google si ya existe
            user.GoogleId  ??= tokenInfo.Sub;
            user.AvatarUrl ??= tokenInfo.Picture;
            user.LastLogin   = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        var token = GenerateJwt(user);
        return Ok(new AuthResponse(token, ToDto(user)));
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

    // ── Helpers ───────────────────────────────────────────────────

    private string GenerateJwt(User user)
    {
        var jwtKey     = Environment.GetEnvironmentVariable("JWT_SECRET")
            ?? _configuration["Jwt:Secret"]
            ?? throw new InvalidOperationException("JWT_SECRET not configured.");

        var key        = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds      = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresDays = int.TryParse(_configuration["Jwt:ExpireDays"], out var d) ? d : 7;

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role),
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
        new(u.Id, u.Name, u.Email, u.AvatarUrl, u.Role);

    // ── Google Token Info DTO ─────────────────────────────────────
    private class GoogleTokenInfo
    {
        public string? Sub     { get; set; } // Google user ID
        public string? Email   { get; set; }
        public string? Name    { get; set; }
        public string? Picture { get; set; }
        public string? Aud     { get; set; } // Audience (your client ID)
    }
}