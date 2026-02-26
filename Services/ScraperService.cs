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
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🚀 Bot execution started");
            
            // Actualizar estado del bot
            bot.Status = "running";
            bot.LastRun = DateTime.UtcNow;
            bot.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "📊 Bot status updated to 'running'");

            // ══════════════════════════════════════════════════════════════
            // FASE 1: Descargar HTML
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, $"🌐 Downloading HTML from: {bot.Url}");
            var html = await DownloadHtmlAsync(bot);
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, $"✅ HTML downloaded: {html.Length:N0} characters");

            // ══════════════════════════════════════════════════════════════
            // FASE 2: Extraer datos embebidos en JSON ANTES de limpiar
            //   Captura __NEXT_DATA__ (Next.js), ld+json, application/json
            //   Esto es CRÍTICO: RemoveIrrelevantElements borra <script>,
            //   así que hay que extraer antes.
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "📦 Extracting embedded JSON data (Next.js, JSON-LD, etc.)");
            var embeddedData = ExtractEmbeddedJsonData(html);
            if (embeddedData.Length > 100)
            {
                await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                    $"✅ Embedded JSON data extracted: {embeddedData.Length:N0} characters");
            }
            else
            {
                await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                    "ℹ️ No embedded JSON data found (site may load data via XHR)");
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 3: Pipeline de limpieza HTML estándar
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🧹 Removing irrelevant elements (scripts, nav, footer…)");
            var filteredHtml = RemoveIrrelevantElements(html);
            var reductionPercentage = html.Length > 0 ? 100 - (filteredHtml.Length * 100 / html.Length) : 0;
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ Elements removed: {filteredHtml.Length:N0} chars (reduced {reductionPercentage}%)");

            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🔍 Extracting relevant content section (scoring)");
            var relevantHtml = ExtractRelevantContent(filteredHtml);
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ Relevant content: {relevantHtml.Length:N0} characters");

            var strippedHtml = StripAttributesFromHtml(relevantHtml);

            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "📝 Converting HTML to compact text (token optimization)");
            var compactText = ConvertToCompactText(strippedHtml);

            // ══════════════════════════════════════════════════════════════
            // FASE 4: Combinar texto HTML + datos embebidos de JSON
            // ══════════════════════════════════════════════════════════════
            if (embeddedData.Length > 100)
            {
                if (compactText.Length < 500)
                {
                    await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                        "📦 HTML text was sparse → using embedded JSON as primary source");
                    compactText = embeddedData;
                }
                else
                {
                    compactText = compactText + "\n\n" + embeddedData;
                }
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 5: Fallback a Playwright si aún no hay texto
            // ══════════════════════════════════════════════════════════════
            if (compactText.Length < 500 && html.Length > 10_000)
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Sparse text ({compactText.Length} chars) — page likely loads data via XHR. Retrying with headless browser…");
                try
                {
                    var (jsHtml, capturedApiData) = await DownloadHtmlWithPlaywrightAsync(bot.Url);
                    
                    // Intentar extraer embedded JSON del HTML renderizado por Playwright
                    var jsEmbedded = ExtractEmbeddedJsonData(jsHtml);
                    
                    // Pipeline estándar sobre el HTML renderizado
                    var jsFiltered = RemoveIrrelevantElements(jsHtml);
                    var jsRelevant = ExtractRelevantContent(jsFiltered);
                    var jsStripped = StripAttributesFromHtml(jsRelevant);
                    var jsText     = ConvertToCompactText(jsStripped);

                    // Combinar todo: texto del DOM + embedded JSON + API interceptada
                    var combined = new StringBuilder();
                    if (jsText.Length > 0) combined.AppendLine(jsText);
                    if (jsEmbedded.Length > 100) combined.AppendLine(jsEmbedded);
                    if (capturedApiData.Length > 100) combined.AppendLine(capturedApiData);

                    var playwrightResult = combined.ToString().Trim();

                    if (playwrightResult.Length > compactText.Length)
                    {
                        compactText = playwrightResult;
                        await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                            $"✅ Headless browser extracted content: {compactText.Length:N0} chars " +
                            $"(DOM: {jsText.Length}, embedded: {jsEmbedded.Length}, API: {capturedApiData.Length})");
                    }
                    else
                    {
                        await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                            $"⚠️ Headless browser also sparse (DOM: {jsText.Length}, embedded: {jsEmbedded.Length}, API: {capturedApiData.Length})");
                    }
                }
                catch (Exception ex)
                {
                    await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                        $"⚠️ Headless browser failed: {ex.Message}");
                }
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 6: Verificar que tenemos contenido
            // ══════════════════════════════════════════════════════════════
            if (compactText.Length < 100)
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    "⚠️ No se pudo extraer texto legible de la página. " +
                    "La página puede requerir autenticación o renderizado JS especial. " +
                    "Abortando — no se enviarán tokens vacíos a Bedrock.");
                bot.Status = "completed";
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

            // ══════════════════════════════════════════════════════════════
            // FASE 7: Enviar a Bedrock
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🤖 Sending to AWS Bedrock (Claude AI) for processing");
            scrapedProperties = await ExtractPropertiesWithBedrock(compactText, bot);
            
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, 
                $"✅ AI processing completed: {scrapedProperties.Count} properties found");
            
            // ══════════════════════════════════════════════════════════════
            // FASE 8: Guardar propiedades en la BD
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "💾 Checking for duplicates and saving new properties");
            int newPropertiesCount = 0;
            int duplicatesCount = 0;
            
            for (int i = 0; i < scrapedProperties.Count; i++)
            {
                var property = scrapedProperties[i];
                
                await _botLogService.SendProgressAsync(bot.Id, bot.Name, i + 1, scrapedProperties.Count, 
                    $"Processing property: {property.Title?.Substring(0, Math.Min(40, property.Title?.Length ?? 0))}...");
                
                if (string.IsNullOrWhiteSpace(property.SourceUrl))
                {
                    property.SourceUrl = bot.Url;
                }

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
                $"💾 Saved {newPropertiesCount} new properties (skipped {duplicatesCount} duplicates)");

            // Actualizar estado del bot
            bot.Status = "completed";
            bot.LastRunCount = scrapedProperties.Count;
            bot.TotalScraped += newPropertiesCount;
            bot.UpdatedAt = DateTime.UtcNow;
            bot.LastError = null;
            await _context.SaveChangesAsync();

            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, 
                $"🎉 Bot execution completed! Scraped: {scrapedProperties.Count} | New: {newPropertiesCount} | Total: {bot.TotalScraped}");
            
            _logger.LogInformation($"Bot {bot.Id} completed. Scraped {scrapedProperties.Count} properties");
        }
        catch (Exception ex)
        {
            await _botLogService.LogErrorAsync(bot.Id, bot.Name, 
                $"❌ Bot execution failed: {ex.Message}", ex);
            
            bot.Status = "error";
            bot.LastError = ex.Message;
            bot.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            
            _logger.LogError(ex, $"Error in bot {bot.Id}");
            throw;
        }

        return scrapedProperties;
    }

    // ══════════════════════════════════════════════════════════════════════
    // EXTRACCIÓN DE DATOS EMBEBIDOS EN JSON
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extrae datos de scripts JSON embebidos ANTES de que RemoveIrrelevantElements
    /// borre los tags <script>. Captura:
    ///   - Next.js: <script id="__NEXT_DATA__">
    ///   - Nuxt.js: <script id="__NUXT_DATA__">
    ///   - JSON-LD: <script type="application/ld+json">
    ///   - Genérico: <script type="application/json">
    /// </summary>
    private static string ExtractEmbeddedJsonData(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var sb = new StringBuilder();

        var scriptNodes = doc.DocumentNode.SelectNodes("//script");
        if (scriptNodes == null) return string.Empty;

        foreach (var script in scriptNodes)
        {
            var scriptType = script.GetAttributeValue("type", "").ToLower();
            var scriptId = script.GetAttributeValue("id", "").ToLower();
            var content = script.InnerText.Trim();

            if (string.IsNullOrEmpty(content) || content.Length < 50)
                continue;

            bool isDataScript =
                scriptId.Contains("__next_data__") ||    // Next.js
                scriptId.Contains("__nuxt") ||           // Nuxt.js
                scriptType == "application/ld+json" ||   // JSON-LD
                scriptType == "application/json";        // Genérico

            if (!isDataScript)
                continue;

            try
            {
                var jsonContent = content.Replace("<!--", "").Replace("-->", "").Trim();
                using var jsonDoc = JsonDocument.Parse(jsonContent);
                var extracted = ExtractTextFromJsonRecursive(jsonDoc.RootElement, maxDepth: 8);

                if (extracted.Length > 100)
                {
                    sb.AppendLine("--- EMBEDDED DATA ---");
                    sb.AppendLine(extracted);
                    sb.AppendLine("--- END ---");
                }
            }
            catch (JsonException)
            {
                // Si no es JSON válido pero tiene keywords inmobiliarios, incluir como texto
                if (content.Length > 200 &&
                    (content.Contains("price", StringComparison.OrdinalIgnoreCase) ||
                     content.Contains("precio", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.AppendLine("--- EMBEDDED DATA ---");
                    sb.AppendLine(content[..Math.Min(content.Length, 50000)]);
                    sb.AppendLine("--- END ---");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Recorre un JsonElement recursivamente extrayendo texto legible.
    /// Filtra assets y campos de config de frameworks.
    /// </summary>
    private static string ExtractTextFromJsonRecursive(JsonElement element, int maxDepth, int currentDepth = 0)
    {
        if (currentDepth > maxDepth) return string.Empty;

        var sb = new StringBuilder();

        // Campos de config de frameworks que no aportan datos útiles
        var skipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "buildid", "assetprefix", "scriptloader", "gsp", "gssp",
            "isfallback", "dynamicids", "customserver", "appgip",
            "__n_ssp", "runtimeconfig", "locale", "locales",
            "defaultlocale", "domainlocales", "icon", "favicon",
            "stylesheet", "chunks", "modules", "webpack",
            "namedchunkgroups", "hash", "contenthash", "entry"
        };

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (skipKeys.Contains(prop.Name))
                        continue;

                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var strVal = prop.Value.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(strVal) && strVal.Length > 1
                            && strVal.Length < 5000 && !IsAssetUrl(strVal))
                        {
                            sb.AppendLine($"{prop.Name}: {strVal}");
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        sb.AppendLine($"{prop.Name}: {prop.Value}");
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        var child = ExtractTextFromJsonRecursive(prop.Value, maxDepth, currentDepth + 1);
                        if (!string.IsNullOrWhiteSpace(child))
                            sb.Append(child);
                    }
                }
                // Separador entre objetos (cada listing)
                if (currentDepth >= 2 && currentDepth <= 4 && sb.Length > 50)
                    sb.AppendLine("---");
                break;

            case JsonValueKind.Array:
                int itemCount = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (itemCount++ > 100) break; // Limitar arrays enormes
                    var child = ExtractTextFromJsonRecursive(item, maxDepth, currentDepth + 1);
                    if (!string.IsNullOrWhiteSpace(child))
                        sb.Append(child);
                }
                break;

            case JsonValueKind.String:
                var val = element.GetString();
                if (!string.IsNullOrWhiteSpace(val) && val.Length > 1 && !IsAssetUrl(val))
                    sb.AppendLine(val);
                break;
        }

        return sb.ToString();
    }

    private static bool IsAssetUrl(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 10)
            return false;

        if (value.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var lower = value.ToLower();
            return lower.Contains(".jpg") || lower.Contains(".jpeg") || lower.Contains(".png")
                || lower.Contains(".gif") || lower.Contains(".webp") || lower.Contains(".svg")
                || lower.Contains(".ico") || lower.Contains(".woff") || lower.Contains(".ttf")
                || lower.Contains(".css") || lower.Contains(".js?")
                || lower.Contains("/_next/") || lower.Contains("/static/")
                || lower.Contains("/chunks/") || lower.Contains("/webpack/");
        }

        return false;
    }

    // ══════════════════════════════════════════════════════════════════════
    // HTML PARSING Y LIMPIEZA
    // ══════════════════════════════════════════════════════════════════════

    private static readonly HashSet<string> _removeByTag = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "svg", "path", "noscript", "head",
        "header", "footer", "nav", "aside", "form", "button",
        "iframe", "meta", "link", "input", "select", "textarea",
        "dialog", "template", "picture", "source", "track",
        "video", "audio", "canvas", "map", "area"
    };

    private static readonly string[] _nonContentKeywords =
    {
        "cookie", "modal", "popup", "overlay", "banner", "advertisement",
        " ads ", "social-share", "newsletter", "subscribe", "sidebar",
        "related-", "breadcrumb", "pagination", "toolbar", "topbar",
        "sticky-", "floating-", "whatsapp", "chat-", "cookie-bar"
    };

    private static readonly string[] _propertyKeywords =
    {
        "dormitorio", "dorm", "baño", "m²", "m2", "uf ", "usd",
        "precio", "bedroom", "bath", "departamento", "casa", "arriendo",
        "venta", "propiedad", "terreno", "oficina", "local", "hectárea",
        "clp", "garage", "estacionamiento"
    };

    private static readonly HashSet<string> _blockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "p", "h1", "h2", "h3", "h4", "h5", "h6",
        "li", "ul", "ol", "article", "section", "main",
        "tr", "td", "th", "blockquote", "pre", "br", "hr"
    };

    private static string RemoveIrrelevantElements(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        doc.DocumentNode.Descendants()
            .Where(n => _removeByTag.Contains(n.Name))
            .ToList()
            .ForEach(n => n.Remove());

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

    private string ExtractRelevantContent(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

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

                if (node.Name is "body" or "html") score /= 3;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestNode  = node;
                }
            }

            if (bestNode != null && bestNode.Name is not ("body" or "html") && bestScore >= 3)
                break;
        }

        return bestNode?.OuterHtml ?? doc.DocumentNode.OuterHtml;
    }

    private string ConvertToCompactText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var sb = new StringBuilder();
        AppendNodeText(doc.DocumentNode, sb);

        var text = Regex.Replace(sb.ToString(), @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

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

        if (tag is "script" or "style" or "svg" or "noscript" or "head") return;

        bool isBlock = _blockElements.Contains(tag);
        if (isBlock) sb.AppendLine();

        string? href = null;
        if (tag == "a")
        {
            href = node.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrEmpty(href) || href.StartsWith("javascript:") || href == "#")
                href = null;
        }

        foreach (var child in node.ChildNodes)
            AppendNodeText(child, sb);

        if (href != null)
            sb.Append($" [{href}]");

        if (isBlock) sb.AppendLine();
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

    // ══════════════════════════════════════════════════════════════════════
    // DESCARGA DE HTML
    // ══════════════════════════════════════════════════════════════════════

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
            var (renderedHtml, _) = await DownloadHtmlWithPlaywrightAsync(bot.Url);
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
            return false;

        return html.Contains("requires JavaScript", StringComparison.OrdinalIgnoreCase)
            || html.Contains("_bmstate", StringComparison.OrdinalIgnoreCase)
            || html.Contains("verifyChallenge", StringComparison.OrdinalIgnoreCase)
            || html.Contains("window.location.reload()", StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PLAYWRIGHT: Descarga con browser headless + intercepción de APIs
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Descarga HTML usando Playwright. Además de renderizar la página:
    /// 1. Intercepta respuestas JSON de APIs internas (fetch/XHR)
    /// 2. Auto-scroll para activar lazy loading
    /// 3. Espera a que haya contenido visible
    /// 
    /// Retorna: (htmlRenderizado, datosApiCapturados)
    /// </summary>
    private static async Task<(string html, string capturedApiData)> DownloadHtmlWithPlaywrightAsync(string url)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage"
            }
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
            Locale = "es-CL",
            TimezoneId = "America/Santiago",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            JavaScriptEnabled = true
        });

        // ── Interceptar respuestas JSON de APIs internas ───────────────
        var capturedJsonResponses = new List<string>();
        var page = await context.NewPageAsync();

        page.Response += async (_, response) =>
        {
            try
            {
                var responseUrl = response.Url;
                var contentType = response.Headers.GetValueOrDefault("content-type", "");
                var status = response.Status;

                bool isJson = contentType.Contains("application/json") || contentType.Contains("text/json");
                bool isDataEndpoint = !responseUrl.Contains(".js")
                    && !responseUrl.Contains(".css")
                    && !responseUrl.Contains("analytics")
                    && !responseUrl.Contains("tracking")
                    && !responseUrl.Contains("google")
                    && !responseUrl.Contains("facebook")
                    && !responseUrl.Contains("hotjar")
                    && !responseUrl.Contains("sentry");

                if (isJson && isDataEndpoint && status >= 200 && status < 300)
                {
                    var body = await response.TextAsync();
                    if (body.Length > 500)
                    {
                        capturedJsonResponses.Add(body);
                    }
                }
            }
            catch { /* Silenciar errores de respuestas ya consumidas */ }
        };

        // ── Navegar ────────────────────────────────────────────────────
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 45000
            });
        }
        catch (TimeoutException) { /* Algunas SPAs nunca llegan a NetworkIdle */ }

        // Esperar rendering
        await page.WaitForTimeoutAsync(3000);

        // ── Auto-scroll ────────────────────────────────────────────────
        try
        {
            await page.EvaluateAsync(@"
                async () => {
                    const delay = (ms) => new Promise(r => setTimeout(r, ms));
                    for (let i = 0; i < 10; i++) {
                        window.scrollBy(0, 400);
                        await delay(500);
                        if ((window.innerHeight + window.scrollY) >= document.body.scrollHeight - 100) break;
                    }
                    window.scrollTo(0, 0);
                    await delay(500);
                }
            ");
        }
        catch { }

        // Esperar contenido visible
        try
        {
            await page.WaitForFunctionAsync(
                "() => document.body && document.body.innerText.length > 500",
                new PageWaitForFunctionOptions { Timeout = 10000 });
        }
        catch (TimeoutException) { }

        var renderedHtml = await page.ContentAsync();

        // ── Procesar JSON capturado de APIs ────────────────────────────
        var apiDataSb = new StringBuilder();
        foreach (var json in capturedJsonResponses)
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(json);
                var text = ExtractTextFromJsonRecursive(jsonDoc.RootElement, maxDepth: 8);
                if (text.Length > 100)
                {
                    apiDataSb.AppendLine("--- CAPTURED API DATA ---");
                    apiDataSb.AppendLine(text);
                    apiDataSb.AppendLine("--- END ---");
                }
            }
            catch { }
        }

        return (renderedHtml, apiDataSb.ToString());
    }

    // ══════════════════════════════════════════════════════════════════════
    // BEDROCK: Extracción con LLM
    // ══════════════════════════════════════════════════════════════════════

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

    private async Task<List<Property>> ExtractPropertiesWithBedrock(string compactText, Bot bot)
    {
        if (IsMockScrapingEnabled())
        {
            await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                "Mock scraping enabled; returning synthetic properties without Bedrock.");
            return BuildMockProperties(bot);
        }

        var modelId = Environment.GetEnvironmentVariable("BEDROCK_MODEL_ID")
            ?? "us.anthropic.claude-3-5-sonnet-20241022-v2:0";

        var chunks = ChunkText(compactText, maxChunkSize: 20_000).ToList();
        await _botLogService.LogInfoAsync(bot.Id, bot.Name,
            $"📦 Content split into {chunks.Count} chunk(s) (model: {modelId})");

        var allProperties = new List<Property>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < chunks.Count; i++)
        {
            var preview = chunks[i].Length > 200 ? chunks[i][..200] + "…" : chunks[i];
            await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                $"🤖 Processing chunk {i + 1}/{chunks.Count} ({chunks[i].Length:N0} chars)...\nPreview: {preview}");

            var chunkProperties = await ProcessChunkWithBedrock(chunks[i], bot, modelId);

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
                $"✅ Chunk {i + 1}: {chunkProperties.Count} found, {added} unique added");
        }

        return allProperties;
    }

    private async Task<List<Property>> ProcessChunkWithBedrock(string chunkText, Bot bot, string modelId)
    {
        var prompt = $@"Analiza el siguiente texto extraído de una página web de bienes raíces chilena.
El texto puede incluir URLs entre corchetes como [https://...].
También puede incluir datos estructurados con formato ""campo: valor"" extraídos de APIs.

Extrae TODAS las propiedades inmobiliarias que encuentres. Para cada una:
- title: Título o descripción de la propiedad
- sourceUrl: URL completa de la propiedad (busca en los [links] del texto o en campos como url, permalink)
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
                MaxTokens = 8000,
                Temperature = 0.1f
            }
        };

        try
        {
            var response = await _bedrockClient.ConverseAsync(request);
            var stopReason = response?.StopReason ?? "unknown";
            var contentBlocks = response?.Output?.Message?.Content;

            var jsonResponse = contentBlocks?
                .Where(b => !string.IsNullOrWhiteSpace(b.Text))
                .Select(b => b.Text)
                .FirstOrDefault();

            await _botLogService.LogDebugAsync(bot.Id, bot.Name,
                $"🔍 Bedrock response: stopReason={stopReason}, textLength={jsonResponse?.Length ?? 0}");

            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Bedrock returned no text (stopReason={stopReason})");
                return new List<Property>();
            }

            jsonResponse = Regex
                .Replace(jsonResponse, @"^```json?\s*|```\s*$", "", RegexOptions.Multiline)
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
                $"⚠️ Invalid JSON from Bedrock: {ex.Message}. Chunk skipped.");
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
                Description = "Mock data generated for testing."
            }
        };
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