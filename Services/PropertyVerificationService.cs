using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Inmobiscrap.Data;
using Inmobiscrap.Models;

namespace Inmobiscrap.Services;

public enum VerificationResult
{
    StillActive,
    Sold,
    NetworkError,
}

public interface IPropertyVerificationService
{
    /// <summary>
    /// Verifica propiedades que no han sido vistas en X días haciendo
    /// HTTP GET a su SourceUrl. Marca como "sold" si 404/redirect/vendido.
    /// </summary>
    Task<(int verified, int sold, int active, int errors)> VerifyStalePropertiesAsync(
        int staleDays = 3, int batchSize = 50, CancellationToken ct = default);

    /// <summary>Verifica una propiedad individual por ID.</summary>
    Task<VerificationResult> VerifySinglePropertyAsync(int propertyId, CancellationToken ct = default);

    /// <summary>Marca manualmente una propiedad como vendida.</summary>
    Task MarkAsSoldAsync(int propertyId);

    /// <summary>Marca manualmente una propiedad como activa (falso positivo).</summary>
    Task MarkAsActiveAsync(int propertyId);
}

public class PropertyVerificationService : IPropertyVerificationService
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PropertyVerificationService> _logger;

    // Palabras clave que indican propiedad vendida/finalizada
    private static readonly string[] _soldKeywords = new[]
    {
        "vendido", "vendida", "no disponible", "publicación finalizada",
        "publicacion finalizada", "aviso finalizado", "propiedad no encontrada",
        "esta propiedad ya no está disponible", "listing has ended",
        "ya fue vendida", "ya se vendió", "no longer available",
        "removed", "expired", "finalizado", "cerrado",
    };

    public PropertyVerificationService(
        ApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<PropertyVerificationService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(int verified, int sold, int active, int errors)> VerifyStalePropertiesAsync(
        int staleDays = 3, int batchSize = 50, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-staleDays);

        // Propiedades candidatas: tienen SourceUrl, no están ya vendidas,
        // y no han sido vistas recientemente
        var candidates = await _context.Properties
            .Where(p => p.SourceUrl != null
                     && p.ListingStatus != "sold"
                     && p.LastSeenAt != null
                     && p.LastSeenAt < cutoff
                     && p.TimesScraped > 0
                     // No verificar si ya se verificó hoy
                     && (p.LastVerifiedAt == null || p.LastVerifiedAt < DateTime.UtcNow.AddHours(-20)))
            .OrderBy(p => p.LastVerifiedAt ?? DateTime.MinValue) // primero las nunca verificadas
            .Take(batchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _logger.LogInformation("PropertyVerification: No stale properties to verify.");
            return (0, 0, 0, 0);
        }

        _logger.LogInformation("PropertyVerification: Verifying {Count} stale properties (cutoff: {Cutoff})",
            candidates.Count, cutoff);

        int sold = 0, active = 0, errors = 0;

        foreach (var property in candidates)
        {
            if (ct.IsCancellationRequested) break;

            var result = await CheckSourceUrlAsync(property.SourceUrl!, ct);

            property.LastVerifiedAt = DateTime.UtcNow;

            switch (result)
            {
                case VerificationResult.Sold:
                    property.ConsecutiveMisses++;
                    if (property.ConsecutiveMisses >= 2 || property.ListingStatus == "missing")
                    {
                        property.ListingStatus = "sold";
                        property.SoldDetectedAt = DateTime.UtcNow;
                        sold++;
                        _logger.LogInformation("PropertyVerification: SOLD - {Title} ({Url})",
                            property.Title, property.SourceUrl);
                    }
                    else
                    {
                        property.ListingStatus = "missing";
                        _logger.LogInformation("PropertyVerification: MISSING (1st miss) - {Title}",
                            property.Title);
                    }
                    break;

                case VerificationResult.StillActive:
                    property.ListingStatus = property.PreviousPrice != null ? "price_changed" : "active";
                    property.ConsecutiveMisses = 0;
                    property.LastSeenAt = DateTime.UtcNow;
                    active++;
                    break;

                case VerificationResult.NetworkError:
                    errors++;
                    break;
            }

            // Rate limiting: 1 request per second
            await Task.Delay(1000, ct);
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "PropertyVerification: Done. Verified={Verified}, Sold={Sold}, Active={Active}, Errors={Errors}",
            candidates.Count, sold, active, errors);

        return (candidates.Count, sold, active, errors);
    }

    public async Task<VerificationResult> VerifySinglePropertyAsync(int propertyId, CancellationToken ct = default)
    {
        var property = await _context.Properties.FindAsync(new object[] { propertyId }, ct);
        if (property == null) return VerificationResult.NetworkError;
        if (string.IsNullOrWhiteSpace(property.SourceUrl)) return VerificationResult.NetworkError;

        var result = await CheckSourceUrlAsync(property.SourceUrl, ct);

        property.LastVerifiedAt = DateTime.UtcNow;

        switch (result)
        {
            case VerificationResult.Sold:
                property.ConsecutiveMisses++;
                property.ListingStatus = property.ConsecutiveMisses >= 2 ? "sold" : "missing";
                if (property.ListingStatus == "sold")
                    property.SoldDetectedAt = DateTime.UtcNow;
                break;

            case VerificationResult.StillActive:
                property.ListingStatus = property.PreviousPrice != null ? "price_changed" : "active";
                property.ConsecutiveMisses = 0;
                property.LastSeenAt = DateTime.UtcNow;
                break;
        }

        await _context.SaveChangesAsync(ct);
        return result;
    }

    public async Task MarkAsSoldAsync(int propertyId)
    {
        var property = await _context.Properties.FindAsync(propertyId);
        if (property == null) return;

        property.ListingStatus = "sold";
        property.SoldDetectedAt = DateTime.UtcNow;
        property.LastVerifiedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task MarkAsActiveAsync(int propertyId)
    {
        var property = await _context.Properties.FindAsync(propertyId);
        if (property == null) return;

        property.ListingStatus = "active";
        property.SoldDetectedAt = null;
        property.ConsecutiveMisses = 0;
        property.LastVerifiedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    // HTTP CHECK
    // ══════════════════════════════════════════════════════════════════════

    private async Task<VerificationResult> CheckSourceUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es-CL,es;q=0.9");

            // No seguir redirects automáticamente para detectar 301→homepage
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var noRedirectClient = new HttpClient(handler);
            noRedirectClient.Timeout = TimeSpan.FromSeconds(15);
            noRedirectClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");

            var response = await noRedirectClient.GetAsync(url, ct);
            var statusCode = (int)response.StatusCode;

            // 404 / 410 → vendida
            if (statusCode == 404 || statusCode == 410)
                return VerificationResult.Sold;

            // 301/302 redirect — verificar si redirige a homepage (vendida)
            if (statusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location?.ToString() ?? "";
                if (IsRedirectToHomepage(url, location))
                    return VerificationResult.Sold;

                // Seguir el redirect y analizar el contenido
                var finalResponse = await client.GetAsync(url, ct);
                if (!finalResponse.IsSuccessStatusCode)
                    return VerificationResult.Sold;

                var body = await finalResponse.Content.ReadAsStringAsync(ct);
                return ContainsSoldKeywords(body) ? VerificationResult.Sold : VerificationResult.StillActive;
            }

            // 200 OK — verificar contenido
            if (response.IsSuccessStatusCode)
            {
                // Re-descargar con redirect habilitado para obtener el body
                var fullResponse = await client.GetAsync(url, ct);
                var body = await fullResponse.Content.ReadAsStringAsync(ct);

                if (body.Length < 500)
                    return VerificationResult.Sold; // Página casi vacía = removida

                return ContainsSoldKeywords(body) ? VerificationResult.Sold : VerificationResult.StillActive;
            }

            // Otros status codes (5xx, etc.)
            return VerificationResult.NetworkError;
        }
        catch (TaskCanceledException)
        {
            return VerificationResult.NetworkError;
        }
        catch (HttpRequestException)
        {
            return VerificationResult.NetworkError;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PropertyVerification: Unexpected error checking {Url}", url);
            return VerificationResult.NetworkError;
        }
    }

    private static bool IsRedirectToHomepage(string originalUrl, string redirectLocation)
    {
        if (string.IsNullOrWhiteSpace(redirectLocation)) return false;

        // Si redirige a la raíz del sitio → vendida
        if (Uri.TryCreate(originalUrl, UriKind.Absolute, out var originalUri)
            && Uri.TryCreate(originalUri, redirectLocation, out var redirectUri))
        {
            var redirectPath = redirectUri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(redirectPath) || redirectPath == "/")
                return true;

            // Redirige a una página genérica de búsqueda/listados
            if (redirectPath.Contains("/buscar") || redirectPath.Contains("/search")
                || redirectPath.Contains("/resultados") || redirectPath.Contains("/listings"))
                return true;
        }

        return false;
    }

    private static bool ContainsSoldKeywords(string htmlBody)
    {
        var lower = htmlBody.ToLowerInvariant();
        return _soldKeywords.Any(keyword => lower.Contains(keyword));
    }
}
