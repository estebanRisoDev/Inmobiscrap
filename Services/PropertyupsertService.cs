using System.Security.Cryptography;
using System.Text;
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
///   1. Busca si ya existe por fingerprint (SHA256 de SourceUrl normalizada).
///   2. Fallback: busca por SourceUrl directamente (resistente a variaciones de URL).
///   3. Si NO existe → la crea + primer snapshot (ambos guardados atómicamente).
///   4. Si SÍ existe → SIEMPRE crea un nuevo snapshot.
///      4a. Compara con los valores actuales de Property.
///      4b. Si algo cambió → marca snapshot.HasChanges = true,
///          actualiza Property con los nuevos valores.
///      4c. Si nada cambió → snapshot.HasChanges = false.
///
/// Cada llamada a UpsertPropertyAsync es completamente autónoma:
/// llama SaveChangesAsync internamente para garantizar atomicidad.
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

    // ── Fingerprint ───────────────────────────────────────────────────────────

    public string GenerateFingerprint(Property property)
    {
        // Fuente primaria: SourceUrl normalizada (más confiable entre runs)
        // Fallback: Title + Address + City
        var raw = !string.IsNullOrWhiteSpace(property.SourceUrl)
            ? property.SourceUrl.Trim().ToLowerInvariant()
            : $"{(property.Title   ?? "").Trim()}|" +
              $"{(property.Address ?? "").Trim()}|" +
              $"{(property.City    ?? "").Trim()}"
                .ToLowerInvariant();

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

    /// <summary>
    /// Normaliza una URL para comparación directa (quita query params, fragment,
    /// trailing slashes y convierte a lowercase).
    /// </summary>
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
        // Necesario cuando Bedrock devuelve URLs con pequeñas variaciones entre
        // runs (query params, mayúsculas, trailing slash) que cambian el fingerprint.
        if (existing == null && !string.IsNullOrWhiteSpace(scraped.SourceUrl))
        {
            var normalizedUrl = NormalizeUrl(scraped.SourceUrl);

            // FIX CS8790: calcular el prefijo FUERA del expression tree
            // (los slices [..n] no pueden traducirse a SQL por EF Core)
            var searchPrefix = normalizedUrl.Length > 30
                ? normalizedUrl[..30]
                : normalizedUrl;

            var candidates = await _context.Properties
                .Where(p => p.SourceUrl != null && p.SourceUrl.ToLower().Contains(searchPrefix))
                .ToListAsync();

            existing = candidates.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.SourceUrl) &&
                NormalizeUrl(p.SourceUrl) == normalizedUrl);

            // Corregir el fingerprint si la URL era la misma pero estaba mal guardada
            if (existing != null && existing.Fingerprint != fingerprint)
            {
                _logger.LogInformation(
                    "Fingerprint corregido para propiedad {Id}: {OldFp} → {NewFp}",
                    existing.Id, existing.Fingerprint, fingerprint);
                existing.Fingerprint = fingerprint;
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
            await _context.SaveChangesAsync(); // Necesario para obtener el Id generado

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
                HasChanges    = false,  // Primer snapshot, no hay "cambio" aún
                ChangedFields = null,
            });

            await _context.SaveChangesAsync(); // Guardar snapshot
            return UpsertResult.New;
        }

        // ══════════════════════════════════════════════════════════════════════
        // CASO 2: Propiedad existente — detectar cambios y crear snapshot
        // ══════════════════════════════════════════════════════════════════════
        existing.LastSeenAt    = now;
        existing.TimesScraped++;
        existing.ListingStatus = "active";

        var changedFields = new List<string>();

        // Precio: solo comparar si ambos tienen precio válido
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

        // Enriquecer campos que antes eran null (nunca sobreescribir con null)
        if (string.IsNullOrEmpty(existing.Region)       && !string.IsNullOrEmpty(scraped.Region))       existing.Region       = scraped.Region;
        if (string.IsNullOrEmpty(existing.Neighborhood) && !string.IsNullOrEmpty(scraped.Neighborhood)) existing.Neighborhood = scraped.Neighborhood;
        if (string.IsNullOrEmpty(existing.Address)      && !string.IsNullOrEmpty(scraped.Address))      existing.Address      = scraped.Address;
        if (string.IsNullOrEmpty(existing.Description)  && !string.IsNullOrEmpty(scraped.Description))  existing.Description  = scraped.Description;

        // ── SIEMPRE crear snapshot — sea que haya cambios o no ────────────────
        // Esto es lo que genera el historial real de observaciones
        var hasChanges = changedFields.Count > 0;

        _context.PropertySnapshots.Add(new PropertySnapshot
        {
            PropertyId    = existing.Id,
            BotId         = botId,
            ScrapedAt     = now,
            // Guardar los valores ACTUALES (ya actualizados si hubo cambios)
            Price         = existing.Price,
            Currency      = existing.Currency,
            Bedrooms      = existing.Bedrooms,
            Bathrooms     = existing.Bathrooms,
            Area          = existing.Area,
            PropertyType  = existing.PropertyType,
            Title         = existing.Title,
            HasChanges    = hasChanges,
            ChangedFields = hasChanges ? string.Join(",", changedFields) : null,
        });

        // Guardar propiedad actualizada + snapshot en una sola transacción
        await _context.SaveChangesAsync();

        return hasChanges ? UpsertResult.Updated : UpsertResult.Unchanged;
    }
}