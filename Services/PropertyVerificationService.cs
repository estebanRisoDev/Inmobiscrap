using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Inmobiscrap.Data;
using Inmobiscrap.Infrastructure;
using Inmobiscrap.Models;
using Inmobiscrap.Hubs;
using Microsoft.AspNetCore.SignalR;

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
    /// batchSize=0 usa batch dinámico basado en el total de propiedades activas.
    /// </summary>
    Task<(int verified, int sold, int active, int errors)> VerifyStalePropertiesAsync(
        int staleDays = 2, int batchSize = 0, CancellationToken ct = default);

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

    // Frases específicas que indican aviso vendido/finalizado. Se buscan SOLO en
    // el texto visible (sin scripts). Antes había palabras sueltas como "removed",
    // "expired", "cerrado", "vendido" o "no disponible", que aparecen en casi
    // cualquier página activa ("condominio cerrado", "más vendidos", JS...) y
    // marcaban como vendidas propiedades que seguían publicadas.
    private static readonly string[] _soldPhrases = new[]
    {
        "publicación finalizada", "publicacion finalizada",
        "publicación pausada", "publicacion pausada",
        "aviso finalizado", "este aviso finalizó", "este aviso finalizo",
        "el aviso ya no está disponible", "el aviso ya no esta disponible",
        "este aviso ya no está disponible", "este aviso ya no esta disponible",
        "esta publicación ya no está disponible", "esta publicacion ya no esta disponible",
        "esta propiedad ya no está disponible", "esta propiedad ya no esta disponible",
        "la propiedad ya no está disponible", "la propiedad ya no esta disponible",
        "propiedad no encontrada", "publicación no encontrada", "publicacion no encontrada",
        "ya fue vendida", "ya fue vendido", "ya se vendió", "ya se vendio",
        "propiedad vendida", "propiedad arrendada",
        "listing has ended", "this listing is no longer available",
    };

    // Clientes compartidos (antes se creaba un HttpClientHandler por request).
    // SafeHttp bloquea IPs privadas/metadata: SourceUrl viene de datos scrapeados.
    private static readonly HttpClient _noRedirectClient = CreateClient(allowRedirect: false);
    private static readonly HttpClient _redirectClient   = CreateClient(allowRedirect: true);

    private static HttpClient CreateClient(bool allowRedirect)
    {
        var client = SafeHttp.CreateClient(allowRedirect, TimeSpan.FromSeconds(25));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es-CL,es;q=0.9");
        return client;
    }

    private readonly IHubContext<BotLogHub> _hubContext;

    public PropertyVerificationService(
        ApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<PropertyVerificationService> logger,
        IHubContext<BotLogHub> hubContext)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _hubContext = hubContext;
    }

    public async Task<(int verified, int sold, int active, int errors)> VerifyStalePropertiesAsync(
        int staleDays = 2, int batchSize = 0, CancellationToken ct = default)
    {
        // Batch dinámico: si batchSize=0, calcular basado en total activas
        if (batchSize <= 0)
        {
            var totalActive = await _context.Properties
                .CountAsync(p => p.ListingStatus != "sold" && p.SourceUrl != null, ct);
            // ~5% del total, mínimo 50, máximo 200
            batchSize = Math.Clamp(totalActive / 20, 50, 200);
        }

        var cutoff = DateTime.UtcNow.AddDays(-staleDays);

        // Propiedades candidatas: tienen SourceUrl, no están ya vendidas,
        // y no han sido vistas recientemente
        var candidates = await _context.Properties
            .Where(p => p.SourceUrl != null
                     && p.ListingStatus != "sold"
                     && p.LastSeenAt != null
                     && p.LastSeenAt < cutoff
                     && p.TimesScraped > 0
                     // No verificar si ya se verificó en las últimas 10 horas
                     && (p.LastVerifiedAt == null || p.LastVerifiedAt < DateTime.UtcNow.AddHours(-10)))
            .OrderBy(p => p.LastVerifiedAt ?? DateTime.MinValue) // primero las nunca verificadas
            .Take(batchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _logger.LogInformation("PropertyVerification: No stale properties to verify.");
            return (0, 0, 0, 0);
        }

        _logger.LogInformation("PropertyVerification: Verifying {Count} stale properties (cutoff: {Cutoff}, batch: {Batch})",
            candidates.Count, cutoff, batchSize);
        await BroadcastVerificationLog($"Iniciando verificación de {candidates.Count} propiedades (stale>{staleDays}d, batch={batchSize})");

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
                        await BroadcastVerificationLog($"VENDIDA: {property.Title}");
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
                    // No incrementar ConsecutiveMisses por error de red (WAF, captcha, downtime temporal)
                    errors++;
                    break;
            }

            // Rate limiting: 1 request per second
            await Task.Delay(1000, ct);
        }

        await _context.SaveChangesAsync(ct);

        var summary = $"Verificación completada: {candidates.Count} verificadas, {sold} vendidas, {active} activas, {errors} errores";
        _logger.LogInformation("PropertyVerification: Done. Verified={Verified}, Sold={Sold}, Active={Active}, Errors={Errors}",
            candidates.Count, sold, active, errors);
        await BroadcastVerificationLog(summary);

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
        if (!SafeHttp.IsHttpUrl(url))
        {
            _logger.LogWarning("PropertyVerification: SourceUrl no es http(s): {Url}", url);
            return VerificationResult.NetworkError;
        }

        try
        {
            // No seguir redirects automáticamente para detectar 301→homepage
            using var response = await _noRedirectClient.GetAsync(url, ct);
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
                using var finalResponse = await _redirectClient.GetAsync(url, ct);
                var finalStatus = (int)finalResponse.StatusCode;
                if (finalStatus is 404 or 410)
                    return VerificationResult.Sold;
                if (!finalResponse.IsSuccessStatusCode)
                    return VerificationResult.NetworkError; // 403/5xx: WAF o caída, no venta

                var redirectedBody = await finalResponse.Content.ReadAsStringAsync(ct);
                return AnalyzeBody(redirectedBody);
            }

            // 200 OK — verificar contenido (se reutiliza el body ya descargado)
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return AnalyzeBody(body);
            }

            // 403 con posible WAF/captcha → tratar como error de red, no como venta
            if (statusCode == 403)
                return VerificationResult.NetworkError;

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

    private static VerificationResult AnalyzeBody(string html)
    {
        var visibleText = ExtractVisibleText(html);

        // Challenge de Cloudflare/captcha → no es indicador de venta
        if (IsWafOrCaptchaPage(html, visibleText))
            return VerificationResult.NetworkError;

        if (html.Length < 500)
            return VerificationResult.Sold; // Página casi vacía = removida

        return _soldPhrases.Any(visibleText.Contains)
            ? VerificationResult.Sold
            : VerificationResult.StillActive;
    }

    /// <summary>Texto visible en minúsculas (sin script/style/noscript/template).</summary>
    private static string ExtractVisibleText(string html)
    {
        try
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);
            doc.DocumentNode
                .Descendants()
                .Where(n => n.Name is "script" or "style" or "noscript" or "template" or "svg")
                .ToList()
                .ForEach(n => n.Remove());

            var text = HtmlAgilityPack.HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").ToLowerInvariant();
        }
        catch
        {
            return html.ToLowerInvariant();
        }
    }

    private static bool IsWafOrCaptchaPage(string html, string visibleText)
    {
        var lower = html.ToLowerInvariant();

        // Marcadores propios de las páginas de challenge (no de un sitio que solo usa el CDN)
        if (lower.Contains("cf-browser-verification")
            || lower.Contains("challenge-platform")
            || lower.Contains("cf-chl-")
            || lower.Contains("<title>just a moment")
            || lower.Contains("<title>attention required"))
            return true;

        // "captcha" solo cuenta si la página casi no tiene contenido (pantalla de bloqueo);
        // muchas páginas normales incluyen reCAPTCHA en su formulario de contacto.
        return visibleText.Length < 1500 && visibleText.Contains("captcha");
    }

    private async Task BroadcastVerificationLog(string message)
    {
        try
        {
            await _hubContext.Clients.Group("Dashboard_Global")
                .SendAsync("ReceiveVerificationLog", new
                {
                    message,
                    timestamp = DateTime.UtcNow,
                });
        }
        catch
        {
            // No fallar la verificación por un error de broadcast
        }
    }
}
