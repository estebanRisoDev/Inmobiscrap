using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Inmobiscrap.Models;

namespace Inmobiscrap.Services;

public enum UpsertResult
{
    New,        // Propiedad nueva + primer snapshot
    Updated,    // Ya existía, se detectaron cambios → snapshot con HasChanges=true
    Unchanged,  // Ya existía, sin cambios → snapshot con HasChanges=false
}

public interface IPropertyUpsertService
{
    Task<UpsertResult> UpsertPropertyAsync(Property scraped, int botId);
    string GenerateFingerprint(Property property);
}

/// <summary>
/// Cada vez que un bot observa una propiedad:
///   1. Busca por fingerprint (SHA256 de SourceUrl normalizada, o Title|City|PropertyType si no hay URL).
///   2. Fallback: busca por SourceUrl directamente.
///   3. Fallback: busca por título + ciudad + tipo normalizado (para
///      propiedades sin URL que el LLM extrajo con variaciones menores).
///   4. Si NO existe → la crea + primer snapshot.
///   5. Si SÍ existe → SIEMPRE crea un nuevo snapshot.
/// </summary>
public class PropertyUpsertService : IPropertyUpsertService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<PropertyUpsertService> _logger;

    public PropertyUpsertService(
        ApplicationDbContext context,
        ILogger<PropertyUpsertService> logger)
    {
        _context = context;
        _logger = logger;
    }

    // ── Normalización de texto ────────────────────────────────────────────────

    /// <summary>
    /// Normaliza un texto para comparación: lowercase, trim, colapsar espacios,
    /// quitar acentos, quitar puntuación.
    /// "  Edificio en Las Condes  " → "edificio en las condes"
    /// "Edificio, Las Condes!" → "edificio las condes"
    /// </summary>
    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim().ToLowerInvariant();
        // Quitar acentos comunes del español
        s = s.Replace("á", "a").Replace("é", "e").Replace("í", "i")
             .Replace("ó", "o").Replace("ú", "u").Replace("ñ", "n")
             .Replace("ü", "u");
        // Quitar puntuación y caracteres especiales
        s = Regex.Replace(s, @"[^\w\s]", " ");
        // Colapsar espacios múltiples
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    // ── Fingerprint ───────────────────────────────────────────────────────────

    public string GenerateFingerprint(Property property)
    {
        var raw = !string.IsNullOrWhiteSpace(property.SourceUrl)
            ? property.SourceUrl.Trim().ToLowerInvariant()
            : $"{NormalizeText(property.Title)}|" +
              $"{NormalizeText(property.City)}|" +
              $"{NormalizeText(property.PropertyType)}|" +
              $"{property.Bedrooms ?? 0}|" +
              $"{property.Area ?? 0}";

        // Normalizar URLs: quitar query params, fragments y trailing slashes
        if (raw.StartsWith("http"))
        {
            try
            {
                var uri = new Uri(raw);
                raw = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
            }
            catch { /* dejar tal cual si la URL es inválida */ }
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim().ToLowerInvariant().TrimEnd('/');
        try
        {
            var uri = new Uri(url);
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
        }
        catch
        {
            return url;
        }
    }

    // ── Upsert principal ──────────────────────────────────────────────────────

    public async Task<UpsertResult> UpsertPropertyAsync(Property scraped, int botId)
    {
        var fingerprint = GenerateFingerprint(scraped);
        scraped.Fingerprint = fingerprint;
        var now = DateTime.UtcNow;

        // ── Paso 1: Buscar por fingerprint ────────────────────────────────────
        var existing = await _context.Properties
            .FirstOrDefaultAsync(p => p.Fingerprint == fingerprint);

        // ── Paso 2: Fallback por SourceUrl normalizada ────────────────────────
        if (existing == null && !string.IsNullOrWhiteSpace(scraped.SourceUrl))
        {
            var normalizedUrl = NormalizeUrl(scraped.SourceUrl);

            var searchPrefix = normalizedUrl.Length > 30
                ? normalizedUrl[..30]
                : normalizedUrl;

            var candidates = await _context.Properties
                .Where(p => p.SourceUrl != null && p.SourceUrl.ToLower().Contains(searchPrefix))
                .ToListAsync();

            existing = candidates.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.SourceUrl) &&
                NormalizeUrl(p.SourceUrl) == normalizedUrl);

            if (existing != null && existing.Fingerprint != fingerprint)
            {
                _logger.LogInformation(
                    "Fingerprint corregido para propiedad {Id}: {OldFp} → {NewFp}",
                    existing.Id, existing.Fingerprint, fingerprint);
                existing.Fingerprint = fingerprint;
            }
        }

        // ── Paso 3: Fallback por título + ciudad + tipo (sin SourceUrl) ───────
        // Cuando Bedrock no encontró la URL individual y el fingerprint se basa
        // en Title|Address|City, variaciones menores del LLM entre chunks
        // generan fingerprints distintos. Este fallback compara los textos
        // normalizados (sin acentos, sin puntuación, lowercase).
        if (existing == null)
        {
            var normTitle = NormalizeText(scraped.Title);
            var normCity  = NormalizeText(scraped.City);
            var normType  = NormalizeText(scraped.PropertyType);

            if (normTitle.Length > 3)
            {
                // Buscar candidatos que compartan ciudad y tipo (si los tiene)
                var candidateQuery = _context.Properties.AsQueryable();

                if (!string.IsNullOrEmpty(normCity))
                    candidateQuery = candidateQuery.Where(p => p.City != null);

                if (!string.IsNullOrEmpty(normType))
                    candidateQuery = candidateQuery.Where(p => p.PropertyType != null);

                // Traer un set acotado para comparar en memoria
                // Usar un prefijo del título para filtrar en SQL
                var titlePrefix = normTitle.Length > 10 ? scraped.Title!.Substring(0, 10) : scraped.Title!;
                var candidates = await candidateQuery
                    .Where(p => p.Title != null && p.Title.ToLower().Contains(titlePrefix.ToLower()))
                    .Take(50)
                    .ToListAsync();

                existing = candidates.FirstOrDefault(p =>
                {
                    var pTitle = NormalizeText(p.Title);
                    var pCity  = NormalizeText(p.City);
                    var pType  = NormalizeText(p.PropertyType);

                    // Título debe ser muy similar (igual normalizado o uno contiene al otro)
                    var titleMatch = pTitle == normTitle
                                  || pTitle.Contains(normTitle)
                                  || normTitle.Contains(pTitle);

                    if (!titleMatch) return false;

                    // Ciudad debe coincidir si ambas existen
                    if (!string.IsNullOrEmpty(normCity) && !string.IsNullOrEmpty(pCity)
                        && pCity != normCity)
                        return false;

                    // Tipo debe coincidir si ambos existen
                    if (!string.IsNullOrEmpty(normType) && !string.IsNullOrEmpty(pType)
                        && pType != normType)
                        return false;

                    return true;
                });

                if (existing != null)
                {
                    _logger.LogInformation(
                        "Propiedad {Id} encontrada por título fuzzy: '{ExistingTitle}' ≈ '{ScrapedTitle}'",
                        existing.Id, existing.Title, scraped.Title);

                    // Actualizar fingerprint para futuras búsquedas directas
                    if (existing.Fingerprint != fingerprint)
                        existing.Fingerprint = fingerprint;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // CASO 1: Propiedad nueva
        // ══════════════════════════════════════════════════════════════════════
        if (existing == null)
        {
            scraped.FirstSeenAt   = now;
            scraped.LastSeenAt    = now;
            scraped.TimesScraped  = 1;
            scraped.ListingStatus = "active";

            _context.Properties.Add(scraped);
            await _context.SaveChangesAsync();

            _context.PropertySnapshots.Add(new PropertySnapshot
            {
                PropertyId    = scraped.Id,
                BotId         = botId,
                ScrapedAt     = now,
                Price         = scraped.Price,
                Currency      = scraped.Currency,
                Bedrooms      = scraped.Bedrooms,
                Bathrooms     = scraped.Bathrooms,
                Area          = scraped.Area,
                PropertyType  = scraped.PropertyType,
                Title         = scraped.Title,
                Region          = scraped.Region,
                City            = scraped.City,
                Neighborhood    = scraped.Neighborhood,
                PublicationDate = scraped.PublicationDate,
                Condition       = scraped.Condition,
                HasChanges      = false,
                ChangedFields   = null,
            });

            await _context.SaveChangesAsync();
            return UpsertResult.New;
        }

        // ══════════════════════════════════════════════════════════════════════
        // CASO 2: Propiedad existente — detectar cambios y crear snapshot
        // ══════════════════════════════════════════════════════════════════════
        existing.LastSeenAt    = now;
        existing.TimesScraped++;
        existing.ListingStatus = "active";

        var changedFields = new List<string>();

        if (scraped.Price.HasValue    && scraped.Price > 0
            && existing.Price.HasValue && existing.Price > 0
            && scraped.Price != existing.Price)
        {
            changedFields.Add("Price");
            existing.PreviousPrice  = existing.Price;
            existing.PriceChangedAt = now;
            existing.Price          = scraped.Price;
            existing.ListingStatus  = "price_changed";
        }

        if (scraped.Bedrooms.HasValue    && scraped.Bedrooms > 0
            && existing.Bedrooms.HasValue
            && scraped.Bedrooms != existing.Bedrooms)
        {
            changedFields.Add("Bedrooms");
            existing.Bedrooms = scraped.Bedrooms;
        }

        if (scraped.Bathrooms.HasValue    && scraped.Bathrooms > 0
            && existing.Bathrooms.HasValue
            && scraped.Bathrooms != existing.Bathrooms)
        {
            changedFields.Add("Bathrooms");
            existing.Bathrooms = scraped.Bathrooms;
        }

        if (scraped.Area.HasValue    && scraped.Area > 0
            && existing.Area.HasValue
            && scraped.Area != existing.Area)
        {
            changedFields.Add("Area");
            existing.Area = scraped.Area;
        }

        if (!string.IsNullOrWhiteSpace(scraped.Currency)
            && !string.IsNullOrWhiteSpace(existing.Currency)
            && scraped.Currency != existing.Currency)
        {
            changedFields.Add("Currency");
            existing.Currency = scraped.Currency;
        }

        if (!string.IsNullOrWhiteSpace(scraped.Title)
            && !string.IsNullOrWhiteSpace(existing.Title)
            && scraped.Title.Trim() != existing.Title.Trim())
        {
            changedFields.Add("Title");
            existing.Title = scraped.Title;
        }

        if (!string.IsNullOrWhiteSpace(scraped.PropertyType)
            && !string.IsNullOrWhiteSpace(existing.PropertyType)
            && scraped.PropertyType != existing.PropertyType)
        {
            changedFields.Add("PropertyType");
            existing.PropertyType = scraped.PropertyType;
        }

        if (!string.IsNullOrWhiteSpace(scraped.Condition)
            && !string.IsNullOrWhiteSpace(existing.Condition)
            && scraped.Condition != existing.Condition)
        {
            changedFields.Add("Condition");
            existing.Condition = scraped.Condition;
        }

        // Enriquecer campos que antes eran null
        if (string.IsNullOrEmpty(existing.Region)       && !string.IsNullOrEmpty(scraped.Region))       existing.Region       = scraped.Region;
        if (string.IsNullOrEmpty(existing.Neighborhood) && !string.IsNullOrEmpty(scraped.Neighborhood)) existing.Neighborhood = scraped.Neighborhood;
        if (string.IsNullOrEmpty(existing.Address)      && !string.IsNullOrEmpty(scraped.Address))      existing.Address      = scraped.Address;
        if (string.IsNullOrEmpty(existing.Description)  && !string.IsNullOrEmpty(scraped.Description))  existing.Description  = scraped.Description;
        if (string.IsNullOrEmpty(existing.SourceUrl)    && !string.IsNullOrEmpty(scraped.SourceUrl))    existing.SourceUrl    = scraped.SourceUrl;
        if (existing.Condition == null && !string.IsNullOrWhiteSpace(scraped.Condition)) existing.Condition = scraped.Condition;
        if (!existing.PublicationDate.HasValue && scraped.PublicationDate.HasValue) existing.PublicationDate = scraped.PublicationDate;

        var hasChanges = changedFields.Count > 0;

        _context.PropertySnapshots.Add(new PropertySnapshot
        {
            PropertyId    = existing.Id,
            BotId         = botId,
            ScrapedAt     = now,
            Price         = existing.Price,
            Currency      = existing.Currency,
            Bedrooms      = existing.Bedrooms,
            Bathrooms     = existing.Bathrooms,
            Area          = existing.Area,
            PropertyType  = existing.PropertyType,
            Title         = existing.Title,
            Region          = existing.Region,
            City            = existing.City,
            Neighborhood    = existing.Neighborhood,
            PublicationDate = existing.PublicationDate,
            Condition       = existing.Condition,
            HasChanges      = hasChanges,
            ChangedFields   = hasChanges ? string.Join(",", changedFields) : null,
        });

        await _context.SaveChangesAsync();

        return hasChanges ? UpsertResult.Updated : UpsertResult.Unchanged;
    }
}