using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
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
    private readonly IBotLogService _botLogService;

    public ScraperService(
        ApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<ScraperService> logger,
        IAmazonBedrockRuntime bedrockClient,
        IBotLogService botLogService)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _bedrockClient = bedrockClient;
        _botLogService = botLogService;
    }

    public async Task<List<Property>> ScrapePropertiesAsync(Bot bot)
    {
        var scrapedProperties = new List<Property>();
        
        try
        {
            // 🔔 LOG: Bot iniciado
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🚀 Bot execution started");
            
            // Actualizar estado del bot
            bot.Status = "running";
            bot.LastRun = DateTime.UtcNow;
            bot.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "📊 Bot status updated to 'running'");

            // 1. Descargar HTML
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, $"🌐 Downloading HTML from: {bot.Url}");
            var html = await DownloadHtmlAsync(bot);
            
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, $"✅ HTML downloaded: {html.Length:N0} characters");
            _logger.LogInformation($"Downloaded HTML: {html.Length} characters");

            // 2. Eliminar elementos irrelevantes (conserva atributos class/id para extracción)
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🧹 Removing irrelevant elements (scripts, nav, footer…)");
            var filteredHtml = RemoveIrrelevantElements(html);
            var reductionPercentage = html.Length > 0 ? 100 - (filteredHtml.Length * 100 / html.Length) : 0;
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ Elements removed: {filteredHtml.Length:N0} chars (reduced {reductionPercentage}%)");

            // 3. Extraer sección de listings (clase y scoring, atributos aún disponibles)
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🔍 Extracting relevant content section (scoring)");
            var relevantHtml = ExtractRelevantContent(filteredHtml);
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ Relevant content: {relevantHtml.Length:N0} characters");

            // 4. Strip atributos (DESPUÉS de extraer, para reducir tokens)
            var strippedHtml = StripAttributesFromHtml(relevantHtml);

            // 5. Convertir a texto compacto (reducción de tokens 80-90%)
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "📝 Converting HTML to compact text (token optimization)");
            var compactText = ConvertToCompactText(strippedHtml);

            // Si el texto es escaso, la página requiere JS → reintentar siempre con Playwright
            // (NO usar strippedHtml como fallback: mandar HTML vacío a Bedrock es inútil)
            if (compactText.Length < 500 && html.Length > 10_000)
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Sparse text ({compactText.Length} chars) — page likely requires JS. Retrying with headless browser…");
                try
                {
                    var jsHtml     = await DownloadHtmlWithPlaywrightAsync(bot.Url);
                    var jsFiltered = RemoveIrrelevantElements(jsHtml);
                    var jsRelevant = ExtractRelevantContent(jsFiltered);
                    var jsStripped = StripAttributesFromHtml(jsRelevant);
                    var jsText     = ConvertToCompactText(jsStripped);

                    if (jsText.Length > compactText.Length)
                    {
                        compactText = jsText;
                        await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                            $"✅ Headless browser extracted content: {compactText.Length:N0} chars");
                    }
                    else
                    {
                        await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                            $"⚠️ Headless browser also produced sparse text ({jsText.Length} chars).");
                    }
                }
                catch (Exception ex)
                {
                    await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                        $"⚠️ Headless browser failed: {ex.Message}");
                }
            }

            // Si después de ambos intentos no hay texto legible → abortar (no enviar HTML vacío a Bedrock)
            if (compactText.Length < 100)
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    "⚠️ No se pudo extraer texto legible de la página. " +
                    "La página puede requerir autenticación o renderizado JS especial. " +
                    "Abortando — no se enviarán tokens vacíos a Bedrock.");
                // Actualizar estado y salir limpiamente
                bot.Status    = "completed";
                bot.LastRunCount = 0;
                bot.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                    "🎉 Bot execution completed. Scraped: 0 | New: 0 | Total: " + bot.TotalScraped);
                return scrapedProperties;
            }

            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ Compact text ready: {compactText.Length:N0} characters");
            _logger.LogInformation($"Compact text: {compactText.Length} characters");

            // 5. Usar Bedrock para extraer propiedades (con chunking automático)
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🤖 Sending to AWS Bedrock (Claude AI) for processing");
            scrapedProperties = await ExtractPropertiesWithBedrock(compactText, bot);
            
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, 
                $"✅ AI processing completed: {scrapedProperties.Count} properties found");
            
            // 5. Guardar propiedades en la BD
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "💾 Checking for duplicates and saving new properties");
            int newPropertiesCount = 0;
            int duplicatesCount = 0;
            
            for (int i = 0; i < scrapedProperties.Count; i++)
            {
                var property = scrapedProperties[i];
                
                // Enviar progreso en tiempo real
                await _botLogService.SendProgressAsync(bot.Id, bot.Name, i + 1, scrapedProperties.Count, 
                    $"Processing property: {property.Title?.Substring(0, Math.Min(40, property.Title.Length))}...");
                
                if (string.IsNullOrWhiteSpace(property.SourceUrl))
                {
                    property.SourceUrl = bot.Url;
                }

                // Verificar si ya existe (por SourceUrl)
                var exists = await _context.Properties.AnyAsync(p => 
                    p.SourceUrl == property.SourceUrl);
                
                if (!exists)
                {
                    _context.Properties.Add(property);
                    newPropertiesCount++;
                    
                    await _botLogService.LogInfoAsync(bot.Id, bot.Name, 
                        $"➕ New property added: {property.Title}");
                }
                else
                {
                    duplicatesCount++;
                    await _botLogService.LogInfoAsync(bot.Id, bot.Name, 
                        $"⏭️ Duplicate skipped: {property.Title}");
                }
            }

            await _context.SaveChangesAsync();
            
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, 
                $"💾 Saved {newPropertiesCount} new properties to database (skipped {duplicatesCount} duplicates)");

            // Actualizar estado del bot
            bot.Status = "completed";
            bot.LastRunCount = scrapedProperties.Count;
            bot.TotalScraped += newPropertiesCount;
            bot.UpdatedAt = DateTime.UtcNow;
            bot.LastError = null; // Limpiar error anterior si había
            await _context.SaveChangesAsync();

            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, 
                $"🎉 Bot execution completed successfully! Scraped: {scrapedProperties.Count} | New: {newPropertiesCount} | Total: {bot.TotalScraped}");
            
            _logger.LogInformation($"Bot {bot.Id} completed. Scraped {scrapedProperties.Count} properties");
        }
        catch (Exception ex)
        {
            await _botLogService.LogErrorAsync(bot.Id, bot.Name, 
                $"❌ Bot execution failed: {ex.Message}", ex);
            
            // Actualizar estado de error
            bot.Status = "error";
            bot.LastError = ex.Message;
            bot.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            
            _logger.LogError(ex, $"Error in bot {bot.Id}");
            throw;
        }

        return scrapedProperties;
    }

    // ── Elementos que nunca contienen listings ─────────────────────────────────
    private static readonly HashSet<string> _removeByTag = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "svg", "path", "noscript", "head",
        "header", "footer", "nav", "aside", "form", "button",
        "iframe", "meta", "link", "input", "select", "textarea",
        "dialog", "template", "picture", "source", "track",
        "video", "audio", "canvas", "map", "area"
    };

    // ── Palabras en class/id que indican contenido no relevante ────────────────
    private static readonly string[] _nonContentKeywords =
    {
        "cookie", "modal", "popup", "overlay", "banner", "advertisement",
        " ads ", "social-share", "newsletter", "subscribe", "sidebar",
        "related-", "breadcrumb", "pagination", "toolbar", "topbar",
        "sticky-", "floating-", "whatsapp", "chat-", "cookie-bar"
    };

    // ── Keywords inmobiliarias para scoring ────────────────────────────────────
    private static readonly string[] _propertyKeywords =
    {
        "dormitorio", "dorm", "baño", "m²", "m2", "uf ", "usd",
        "precio", "bedroom", "bath", "departamento", "casa", "arriendo",
        "venta", "propiedad", "terreno", "oficina", "local", "hectárea",
        "clp", "garage", "estacionamiento"
    };

    // ── Elementos de bloque para saltos de línea al convertir a texto ──────────
    private static readonly HashSet<string> _blockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "p", "h1", "h2", "h3", "h4", "h5", "h6",
        "li", "ul", "ol", "article", "section", "main",
        "tr", "td", "th", "blockquote", "pre", "br", "hr"
    };

    /// <summary>
    /// Paso 1: Elimina elementos que nunca contienen listings y secciones con
    /// class/id de contenido irrelevante. CONSERVA los atributos class/id para
    /// que ExtractRelevantContent pueda usarlos en sus XPath queries.
    /// </summary>
    private static string RemoveIrrelevantElements(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // 1a. Eliminar por tipo de tag
        doc.DocumentNode.Descendants()
            .Where(n => _removeByTag.Contains(n.Name))
            .ToList()
            .ForEach(n => n.Remove());

        // 1b. Eliminar por class/id que sugieran contenido irrelevante
        doc.DocumentNode.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Element)
            .Where(n =>
            {
                var cls = n.GetAttributeValue("class", "").ToLower();
                var id  = n.GetAttributeValue("id",    "").ToLower();
                return _nonContentKeywords.Any(kw => cls.Contains(kw) || id.Contains(kw));
            })
            .ToList()
            .ForEach(n => n.Remove());

        return doc.DocumentNode.OuterHtml;
    }

    /// <summary>
    /// Paso 3 (post-extracción): Strip todos los atributos excepto href en &lt;a&gt;.
    /// Debe ejecutarse DESPUÉS de ExtractRelevantContent para no romper las XPath queries.
    /// </summary>
    private static string StripAttributesFromHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var node in doc.DocumentNode.Descendants().ToList())
        {
            if (node.NodeType != HtmlNodeType.Element) continue;

            if (node.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                var href = node.GetAttributeValue("href", "").Trim();
                node.Attributes.RemoveAll();
                if (!string.IsNullOrEmpty(href) && !href.StartsWith("javascript:"))
                    node.SetAttributeValue("href", href);
            }
            else
            {
                node.Attributes.RemoveAll();
            }
        }

        return doc.DocumentNode.OuterHtml;
    }

    /// <summary>
    /// Extrae la sección de listings usando múltiples estrategias con scoring
    /// basado en densidad de keywords inmobiliarias. Evita mandar el DOM completo.
    /// </summary>
    private string ExtractRelevantContent(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Candidatos XPath en orden de preferencia semántica
        var xpathCandidates = new[]
        {
            "//main",
            "//div[contains(@class,'listing')]",
            "//div[contains(@class,'result')]",
            "//div[contains(@class,'property')]",
            "//div[contains(@class,'inmueble')]",
            "//div[contains(@class,'propiedad')]",
            "//div[contains(@class,'search-result')]",
            "//div[contains(@class,'grid')]",
            "//section[contains(@class,'listing')]",
            "//section[contains(@class,'result')]",
            "//ul[contains(@class,'listing')]",
            "//ul[contains(@class,'result')]",
            "//article",
            "//section",
            "//body",
        };

        HtmlNode? bestNode  = null;
        int       bestScore = 0;

        foreach (var xpath in xpathCandidates)
        {
            var nodes = doc.DocumentNode.SelectNodes(xpath);
            if (nodes == null) continue;

            foreach (var node in nodes)
            {
                var text  = node.InnerText.ToLower();
                var score = _propertyKeywords.Sum(kw => CountOccurrences(text, kw));

                // Penalizar body/html para preferir contenedores más específicos
                if (node.Name is "body" or "html") score /= 3;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestNode  = node;
                }
            }

            // Si encontramos algo bueno fuera de body/html, detenemos la búsqueda
            if (bestNode != null && bestNode.Name is not ("body" or "html") && bestScore >= 3)
                break;
        }

        return bestNode?.OuterHtml ?? doc.DocumentNode.OuterHtml;
    }

    /// <summary>
    /// Convierte HTML a texto compacto y legible.
    /// Conserva URLs de los &lt;a href&gt; para SourceUrl.
    /// Reducción típica de tokens: 80–90%.
    /// </summary>
    private string ConvertToCompactText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var sb = new StringBuilder();
        AppendNodeText(doc.DocumentNode, sb);

        // Normalizar espacios y saltos de línea
        var text = Regex.Replace(sb.ToString(), @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        // Filtrar líneas vacías o de un solo carácter (basura residual)
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 1);

        return string.Join("\n", lines).Trim();
    }

    private static void AppendNodeText(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
                sb.Append(text.Trim()).Append(' ');
            return;
        }

        if (node.NodeType != HtmlNodeType.Element) return;

        var tag = node.Name.ToLower();

        // Saltar elementos que no aportan texto visible
        if (tag is "script" or "style" or "svg" or "noscript" or "head") return;

        bool isBlock = _blockElements.Contains(tag);
        if (isBlock) sb.AppendLine();

        // Capturar href antes de procesar hijos
        string? href = null;
        if (tag == "a")
        {
            href = node.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrEmpty(href) || href.StartsWith("javascript:") || href == "#")
                href = null;
        }

        foreach (var child in node.ChildNodes)
            AppendNodeText(child, sb);

        // Agregar URL al final del contenido del enlace
        if (href != null)
            sb.Append($" [{href}]");

        if (isBlock) sb.AppendLine();
    }

    /// <summary>
    /// Divide texto largo en chunks que respetan límites de párrafo.
    /// Evita cortar a mitad de una propiedad.
    /// </summary>
    private static IEnumerable<string> ChunkText(string text, int maxChunkSize = 20_000)
    {
        if (text.Length <= maxChunkSize)
        {
            yield return text;
            yield break;
        }

        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + maxChunkSize, text.Length);

            // Intentar cortar en doble salto de línea para no truncar a mitad de propiedad
            if (end < text.Length)
            {
                int breakPos = text.LastIndexOf("\n\n", end, Math.Min(3000, end - start));
                if (breakPos > start + maxChunkSize / 2)
                    end = breakPos;
            }

            yield return text[start..end].Trim();
            start = end;
        }
    }

    private static int CountOccurrences(string text, string keyword)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += keyword.Length;
        }
        return count;
    }

    /// <summary>
    /// Envía el texto compacto a Bedrock en chunks para extraer propiedades.
    /// Maneja páginas grandes sin explotar el presupuesto de tokens.
    /// </summary>
    private async Task<List<Property>> ExtractPropertiesWithBedrock(string compactText, Bot bot)
    {
        if (IsMockScrapingEnabled())
        {
            await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                "Mock scraping enabled; returning synthetic properties without Bedrock.");
            return BuildMockProperties(bot);
        }

        // Leer model ID desde variable de entorno; por defecto usa cross-region inference profile
        // que funciona en todas las regiones US sin configuración adicional.
        var modelId = Environment.GetEnvironmentVariable("BEDROCK_MODEL_ID")
            ?? "us.anthropic.claude-3-5-sonnet-20241022-v2:0";

        var chunks = ChunkText(compactText, maxChunkSize: 20_000).ToList();
        await _botLogService.LogInfoAsync(bot.Id, bot.Name,
            $"📦 Contenido dividido en {chunks.Count} chunk(s) para procesamiento (modelo: {modelId})");

        var allProperties = new List<Property>();
        var seenUrls      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < chunks.Count; i++)
        {
            // Preview del contenido del chunk para diagnóstico
            var preview = chunks[i].Length > 200 ? chunks[i][..200] + "…" : chunks[i];
            await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                $"🤖 Procesando chunk {i + 1}/{chunks.Count} con Bedrock ({chunks[i].Length:N0} chars)...\nPreview: {preview}");

            var chunkProperties = await ProcessChunkWithBedrock(chunks[i], bot, modelId);

            // Deduplicar por SourceUrl entre chunks
            int added = 0;
            foreach (var prop in chunkProperties)
            {
                var key = string.IsNullOrWhiteSpace(prop.SourceUrl) ? prop.Title : prop.SourceUrl;
                if (key != null && seenUrls.Add(key))
                {
                    allProperties.Add(prop);
                    added++;
                }
            }

            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ Chunk {i + 1}: {chunkProperties.Count} propiedades encontradas, {added} únicas agregadas");
        }

        return allProperties;
    }

    private async Task<List<Property>> ProcessChunkWithBedrock(string chunkText, Bot bot, string modelId)
    {
        var prompt = $@"Analiza el siguiente texto extraído de una página web de bienes raíces chilena.
El texto puede incluir URLs entre corchetes como [https://...].

Extrae TODAS las propiedades inmobiliarias que encuentres. Para cada una:
- title: Título o descripción de la propiedad
- sourceUrl: URL completa de la propiedad (busca en los [links] del texto)
- price: Precio (solo número, sin puntos ni comas)
- currency: Moneda detectada (CLP, UF, USD) — por defecto CLP
- address: Dirección
- city: Ciudad
- region: Región o estado
- neighborhood: Barrio o comuna
- bedrooms: Número de dormitorios (entero)
- bathrooms: Número de baños (entero)
- area: Superficie en m² (número decimal)
- propertyType: Tipo (departamento, casa, oficina, terreno, local, etc.)
- description: Descripción breve

Reglas:
- Si un campo no está presente, usa null (no inventes datos)
- sourceUrl debe ser la URL real de la propiedad, no la del sitio general
- price debe ser solo el número (ej: 150000000 para $150.000.000, o 4500 para UF 4.500)
- Incluye TODAS las propiedades que veas, no omitas ninguna

Responde ÚNICAMENTE con JSON válido, sin texto adicional:
{{
  ""properties"": [
    {{
      ""title"": ""Departamento 3D en Providencia"",
      ""sourceUrl"": ""https://example.com/prop/123"",
      ""price"": 4500,
      ""currency"": ""UF"",
      ""address"": ""Av. Providencia 456"",
      ""city"": ""Santiago"",
      ""region"": ""Metropolitana"",
      ""neighborhood"": ""Providencia"",
      ""bedrooms"": 3,
      ""bathrooms"": 2,
      ""area"": 85.0,
      ""propertyType"": ""departamento"",
      ""description"": ""Amplio departamento con terraza""
    }}
  ]
}}

TEXTO:
{chunkText}";

        var request = new ConverseRequest
        {
            ModelId = modelId,
            Messages = new List<Message>
            {
                new Message
                {
                    Role = "user",
                    Content = new List<ContentBlock>
                    {
                        new ContentBlock { Text = prompt }
                    }
                }
            },
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens = 8000,   // Aumentado para respuestas largas con muchas propiedades
                Temperature = 0.1f  // Temperatura baja para mayor precisión y consistencia
            }
        };

        try
        {
            var response      = await _bedrockClient.ConverseAsync(request);
            var stopReason    = response?.StopReason ?? "unknown";
            var contentBlocks = response?.Output?.Message?.Content;

            // Buscar el primer bloque con texto (puede haber múltiples bloques)
            var jsonResponse = contentBlocks?
                .Where(b => !string.IsNullOrWhiteSpace(b.Text))
                .Select(b => b.Text)
                .FirstOrDefault();

            await _botLogService.LogDebugAsync(bot.Id, bot.Name,
                $"🔍 Bedrock response: stopReason={stopReason}, blocks={contentBlocks?.Count ?? 0}, textLength={jsonResponse?.Length ?? 0}");

            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Bedrock returned no text content (stopReason={stopReason}). " +
                    $"Possible causes: content filter, guardrail, max_tokens exceeded, or model unavailable.");
                return new List<Property>();
            }

            // Limpiar markdown si viene con ```json ... ```
            jsonResponse = Regex
                .Replace(jsonResponse, @"^```json?\s*|```\s*$", "",
                    RegexOptions.Multiline)
                .Trim();

            var result = JsonSerializer.Deserialize<BedrockResponse>(jsonResponse, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result?.Properties?.Select(p => new Property
            {
                Title        = p.Title ?? string.Empty,
                SourceUrl    = string.IsNullOrWhiteSpace(p.SourceUrl) ? bot.Url : p.SourceUrl,
                Price        = p.Price,
                Currency     = p.Currency ?? "CLP",
                Address      = p.Address,
                City         = p.City,
                Region       = p.Region,
                Neighborhood = p.Neighborhood,
                Bedrooms     = p.Bedrooms,
                Bathrooms    = p.Bathrooms,
                Area         = p.Area,
                PropertyType = p.PropertyType,
                Description  = p.Description
            }).ToList() ?? new List<Property>();
        }
        catch (JsonException ex)
        {
            await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                $"⚠️ JSON inválido en respuesta de Bedrock: {ex.Message}. Chunk omitido.");
            return new List<Property>();
        }
        catch (Exception ex)
        {
            await _botLogService.LogErrorAsync(bot.Id, bot.Name,
                $"❌ Bedrock API error ({ex.GetType().Name}): {ex.Message}", ex);
            return new List<Property>();
        }
    }

    private static bool IsMockScrapingEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("SCRAPER_MOCK_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static List<Property> BuildMockProperties(Bot bot)
    {
        return new List<Property>
        {
            new Property
            {
                Title = $"Mock property for {bot.Name}",
                SourceUrl = bot.Url,
                Price = 100000000,
                Currency = "CLP",
                Address = "Av. Providencia 123",
                City = "Santiago",
                Region = "Metropolitana",
                Neighborhood = "Providencia",
                Bedrooms = 3,
                Bathrooms = 2,
                Area = 85,
                PropertyType = "casa",
                Description = "Mock data generated for testing the pipeline."
            }
        };
    }

    private async Task<string> DownloadHtmlAsync(Bot bot)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es-CL,es;q=0.9");

        var html = await client.GetStringAsync(bot.Url);
        if (!IsJavascriptChallenge(html))
        {
            return html;
        }

        await _botLogService.LogWarningAsync(bot.Id, bot.Name,
            "Detected JavaScript challenge page; retrying with headless browser.");

        try
        {
            var renderedHtml = await DownloadHtmlWithPlaywrightAsync(bot.Url);
            if (!string.IsNullOrWhiteSpace(renderedHtml))
            {
                return renderedHtml;
            }
        }
        catch (Exception ex)
        {
            await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                $"Playwright failed to render page: {ex.Message}");
        }

        return html;
    }

    private static bool IsJavascriptChallenge(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return html.Contains("requires JavaScript", StringComparison.OrdinalIgnoreCase)
            || html.Contains("_bmstate", StringComparison.OrdinalIgnoreCase)
            || html.Contains("verifyChallenge", StringComparison.OrdinalIgnoreCase)
            || html.Contains("window.location.reload()", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> DownloadHtmlWithPlaywrightAsync(string url)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
            Locale = "es-CL",
            TimezoneId = "America/Santiago"
        });

        var page = await context.NewPageAsync();
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60000
        });

        return await page.ContentAsync();
    }

    // Clases auxiliares para deserialización
    private class BedrockResponse
    {
        public List<PropertyDto>? Properties { get; set; }
    }

    private class PropertyDto
    {
        public string? Title { get; set; }
        public string? SourceUrl { get; set; }
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
