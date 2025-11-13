using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using HtmlAgilityPack;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Inmobiscrap.Data;
using Inmobiscrap.Models;

namespace Inmobiscrap.Services;

public interface IScraperService
{
    Task<List<Property>> ScrapePropertiesAsync(Bot bot);
}

public class ScraperService : IScraperService
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ScraperService> _logger;
    private readonly IAmazonBedrockRuntime _bedrockClient;

    public ScraperService(
        ApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<ScraperService> logger,
        IAmazonBedrockRuntime bedrockClient)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _bedrockClient = bedrockClient;
    }

    public async Task<List<Property>> ScrapePropertiesAsync(Bot bot)
    {
        var scrapedProperties = new List<Property>();
        
        try
        {
            // Actualizar estado del bot
            bot.Status = "running";
            bot.LastRun = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // 1. Descargar HTML
            var client = _httpClientFactory.CreateClient();
            var html = await client.GetStringAsync(bot.Url);
            
            _logger.LogInformation($"Downloaded HTML: {html.Length} characters");

            // 2. Limpiar HTML (eliminar basura genérica)
            var cleanedHtml = CleanHtml(html);
            
            _logger.LogInformation($"Cleaned HTML: {cleanedHtml.Length} characters (reduced {100 - (cleanedHtml.Length * 100 / html.Length)}%)");

            // 3. Extraer contenido relevante (solo sección de listados)
            var relevantHtml = ExtractRelevantContent(cleanedHtml);
            
            _logger.LogInformation($"Relevant HTML: {relevantHtml.Length} characters");

            // 4. Usar Bedrock para extraer propiedades estructuradas
            scrapedProperties = await ExtractPropertiesWithBedrock(relevantHtml, bot);
            
            // 5. Guardar propiedades en la BD
            int newPropertiesCount = 0;
            foreach (var property in scrapedProperties)
            {
                // Verificar duplicados
                var exists = await _context.Properties
                    .AnyAsync(p => p.Title == property.Title && p.Address == property.Address);
                
                if (!exists)
                {
                    _context.Properties.Add(property);
                    newPropertiesCount++;
                }
            }
            
            await _context.SaveChangesAsync();
            
            // 6. Actualizar estadísticas del bot
            bot.Status = "completed";
            bot.LastRunCount = newPropertiesCount;
            bot.TotalScraped += newPropertiesCount;
            bot.LastError = null;
            bot.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            
            _logger.LogInformation(
                $"Bot '{bot.Name}' completed: {scrapedProperties.Count} found, {newPropertiesCount} new");
        }
        catch (Exception ex)
        {
            bot.Status = "error";
            bot.LastError = ex.Message;
            bot.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            
            _logger.LogError(ex, $"Error scraping with bot '{bot.Name}'");
        }
        
        return scrapedProperties;
    }

    /// <summary>
    /// Limpia el HTML eliminando elementos genéricos que no aportan valor
    /// </summary>
    private string CleanHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Lista de elementos a eliminar
        var tagsToRemove = new[]
        {
            "script",      // JavaScript
            "style",       // CSS inline
            "noscript",    // Contenido sin JS
            "iframe",      // Iframes
            "nav",         // Navegación
            "header",      // Headers
            "footer",      // Footers
            "aside",       // Sidebars
            "form",        // Formularios (búsqueda, login, etc)
            "svg",         // Iconos SVG
            "link",        // Links a CSS
            "meta",        // Metadatos
            "img"          // Imágenes
        };

        foreach (var tag in tagsToRemove)
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
            if (nodes != null)
            {
                foreach (var node in nodes.ToList())
                {
                    node.Remove();
                }
            }
        }

        // Eliminar comentarios HTML
        var comments = doc.DocumentNode.SelectNodes("//comment()");
        if (comments != null)
        {
            foreach (var comment in comments.ToList())
            {
                comment.Remove();
            }
        }

        // Eliminar atributos innecesarios (mantener solo id, class para análisis)
        var attributesToRemove = new[]
        {
            "style",
            "onclick",
            "onload",
            "onerror",
            "data-track",
            "data-analytics",
            "aria-label",
            "aria-hidden"
        };

        var allNodes = doc.DocumentNode.SelectNodes("//*");
        if (allNodes != null)
        {
            foreach (var node in allNodes)
            {
                foreach (var attr in attributesToRemove)
                {
                    node.Attributes.Remove(attr);
                }
            }
        }

        return doc.DocumentNode.OuterHtml;
    }

    /// <summary>
    /// Extrae solo el contenido relevante (sección de listados de propiedades)
    /// </summary>
    private string ExtractRelevantContent(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Intentar encontrar el contenedor principal de listados
        var possibleSelectors = new[]
        {
            "//main",
            "//div[contains(@class, 'listings')]",
            "//div[contains(@class, 'properties')]",
            "//div[contains(@class, 'results')]",
            "//div[contains(@class, 'search-results')]",
            "//section[contains(@class, 'properties')]",
            "//ul[contains(@class, 'property-list')]",
            "//div[@id='results']",
            "//div[@id='listings']"
        };

        foreach (var selector in possibleSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            if (node != null && node.InnerHtml.Length > 1000)
            {
                _logger.LogInformation($"Found relevant content with selector: {selector}");
                return node.OuterHtml;
            }
        }

        // Si no encontramos nada específico, retornar el body limpio
        var body = doc.DocumentNode.SelectSingleNode("//body");
        return body?.OuterHtml ?? html;
    }

    /// <summary>
    /// Usa AWS Bedrock con Claude para extraer propiedades del HTML
    /// </summary>
    private async Task<List<Property>> ExtractPropertiesWithBedrock(string html, Bot bot)
    {
        // Truncar HTML si es muy largo
        const int maxHtmlLength = 150000;
        if (html.Length > maxHtmlLength)
        {
            html = html.Substring(0, maxHtmlLength);
            _logger.LogWarning($"HTML truncated to {maxHtmlLength} characters");
        }

        // 🔍 LOG: Ver qué HTML estamos enviando
        _logger.LogInformation($"📤 HTML being sent to Bedrock (first 2000 chars):\n{html.Substring(0, Math.Min(2000, html.Length))}");

        var prompt = $@"Eres un experto extractor de información de propiedades inmobiliarias.

Analiza el siguiente HTML y extrae TODAS las propiedades que encuentres.

Para cada propiedad, extrae:
- title: título o nombre de la propiedad
- price: precio (solo el número, sin símbolos ni puntos)
- currency: moneda (CLP, USD, UF)
- address: dirección completa
- city: ciudad
- region: región
- neighborhood: barrio o comuna
- bedrooms: número de dormitorios (número entero)
- bathrooms: número de baños (número entero)
- area: área en metros cuadrados (número decimal)
- propertyType: tipo (departamento, casa, oficina, local, terreno, otro)
- description: descripción breve

IMPORTANTE:
- Responde ÚNICAMENTE con un JSON array válido
- NO agregues texto antes o después del JSON
- Si no encuentras un campo, usa null
- Los números deben ser numéricos, no strings

Formato de respuesta:
[
  {{
    ""title"": ""Hermoso departamento en Providencia"",
    ""price"": 150000000,
    ""currency"": ""CLP"",
    ""address"": ""Av. Providencia 1234"",
    ""city"": ""Providencia"",
    ""region"": ""Metropolitana"",
    ""neighborhood"": ""Providencia"",
    ""bedrooms"": 3,
    ""bathrooms"": 2,
    ""area"": 85.5,
    ""propertyType"": ""departamento"",
    ""description"": ""Amplio departamento con vista""
  }}
]

HTML:
{html}";

        try
        {
            var request = new InvokeModelRequest
            {
                ModelId = "us.anthropic.claude-sonnet-4-5-20250929-v1:0",
                ContentType = "application/json",
                Accept = "application/json",
                Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    anthropic_version = "bedrock-2023-05-31",
                    max_tokens = 8192,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = prompt
                        }
                    }
                })))
            };

            _logger.LogInformation("🚀 Sending request to AWS Bedrock...");

            var response = await _bedrockClient.InvokeModelAsync(request);

            using var reader = new StreamReader(response.Body);
            var responseBody = await reader.ReadToEndAsync();

            // 🔍 LOG: Ver respuesta completa de Bedrock
            _logger.LogInformation($"📥 Bedrock RAW response:\n{responseBody}");

            // 🔧 FIX CRÍTICO: Agregar PropertyNameCaseInsensitive para deserializar correctamente
            var bedrockResponse = JsonSerializer.Deserialize<BedrockResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (bedrockResponse?.Content == null || bedrockResponse.Content.Length == 0)
            {
                _logger.LogWarning("⚠️ Empty response from Bedrock - Content array is null or empty");
                return new List<Property>();
            }

            // Verificar si la respuesta fue truncada
            if (bedrockResponse.StopReason == "max_tokens")
            {
                _logger.LogWarning("⚠️ Response was truncated due to max_tokens limit");
            }

            var contentText = bedrockResponse.Content[0].Text;

            // 🔍 LOG: Ver el texto de respuesta de Claude
            _logger.LogInformation($"📝 Claude response text (first 1000 chars):\n{contentText.Substring(0, Math.Min(1000, contentText.Length))}");

            // Limpiar markdown
            contentText = contentText.Trim();
            if (contentText.StartsWith("```json"))
            {
                contentText = contentText.Substring(7);
            }
            if (contentText.StartsWith("```"))
            {
                contentText = contentText.Substring(3);
            }
            if (contentText.EndsWith("```"))
            {
                contentText = contentText.Substring(0, contentText.Length - 3);
            }
            contentText = contentText.Trim();

            _logger.LogInformation($"🧹 Cleaned JSON text (first 500 chars):\n{contentText.Substring(0, Math.Min(500, contentText.Length))}");

            // Parsear JSON con manejo de errores robusto
            return ParseJsonWithFallback(contentText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error calling AWS Bedrock");
            throw;
        }
    }

    /// <summary>
    /// Intenta parsear JSON, y si falla, rescata las propiedades válidas
    /// </summary>
    private List<Property> ParseJsonWithFallback(string jsonText)
    {
        try
        {
            // Intento 1: Parsear JSON completo
            var propertyDtos = JsonSerializer.Deserialize<List<PropertyDto>>(jsonText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _logger.LogInformation($"✅ Successfully parsed {propertyDtos?.Count ?? 0} properties");
            return ConvertDtosToProperties(propertyDtos);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "⚠️ JSON parsing failed, attempting to repair...");
            
            // Intento 2: Reparar JSON incompleto
            var repairedJson = RepairIncompleteJson(jsonText);
            
            try
            {
                var propertyDtos = JsonSerializer.Deserialize<List<PropertyDto>>(repairedJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                _logger.LogInformation($"✅ Successfully recovered {propertyDtos?.Count ?? 0} properties from incomplete JSON");
                return ConvertDtosToProperties(propertyDtos);
            }
            catch (JsonException repairEx)
            {
                _logger.LogError(repairEx, "❌ Could not repair JSON, returning empty list");
                _logger.LogDebug($"Failed JSON content: {jsonText}");
                return new List<Property>();
            }
        }
    }

    /// <summary>
    /// Intenta reparar JSON incompleto cerrando arrays y objetos
    /// </summary>
    private string RepairIncompleteJson(string incompleteJson)
    {
        var repaired = incompleteJson.Trim();
        
        // Contar llaves y corchetes abiertos
        int openBraces = repaired.Count(c => c == '{');
        int closeBraces = repaired.Count(c => c == '}');
        int openBrackets = repaired.Count(c => c == '[');
        int closeBrackets = repaired.Count(c => c == ']');
        
        // Si está dentro de un objeto incompleto, eliminar hasta el último objeto completo
        if (openBraces > closeBraces)
        {
            // Encontrar el último objeto completo
            var lastCompleteObject = repaired.LastIndexOf("},");
            if (lastCompleteObject > 0)
            {
                repaired = repaired.Substring(0, lastCompleteObject + 1);
            }
        }
        
        // Cerrar el array si está abierto
        if (openBrackets > closeBrackets)
        {
            repaired += "\n]";
        }
        
        _logger.LogDebug($"🔧 Repaired JSON: {(repaired.Length > 500 ? repaired.Substring(0, 500) + "..." : repaired)}");
        return repaired;
    }

    /// <summary>
    /// Convierte DTOs a entidades Property
    /// </summary>
    private List<Property> ConvertDtosToProperties(List<PropertyDto>? propertyDtos)
    {
        if (propertyDtos == null)
            return new List<Property>();
        
        return propertyDtos.Select(dto => new Property
        {
            Title = dto.Title ?? string.Empty,
            Price = dto.Price,
            Currency = dto.Currency ?? "CLP",
            Address = dto.Address,
            City = dto.City,
            Region = dto.Region,
            Neighborhood = dto.Neighborhood,
            Bedrooms = dto.Bedrooms,
            Bathrooms = dto.Bathrooms,
            Area = dto.Area,
            PropertyType = dto.PropertyType,
            Description = dto.Description
        }).ToList();
    }

    // DTOs para deserialización de respuesta de Bedrock
    private class BedrockResponse
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Role { get; set; }
        public ContentBlock[]? Content { get; set; }
        public string? Model { get; set; }
        public string? StopReason { get; set; }
    }

    private class ContentBlock
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
    }

    private class PropertyDto
    {
        public string? Title { get; set; }
        public decimal? Price { get; set; }
        public string? Currency { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Region { get; set; }
        public string? Neighborhood { get; set; }
        public int? Bedrooms { get; set; }
        public int? Bathrooms { get; set; }
        public decimal? Area { get; set; }
        public string? PropertyType { get; set; }
        public string? Description { get; set; }
    }
}