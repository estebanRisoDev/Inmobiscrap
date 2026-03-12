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
    private readonly IPropertyUpsertService _upsertService;

    public ScraperService(
        ApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<ScraperService> logger,
        IAmazonBedrockRuntime bedrockClient,
        IBotLogService botLogService,
        IPropertyUpsertService upsertService)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _bedrockClient = bedrockClient;
        _botLogService = botLogService;
        _upsertService = upsertService;
    }

    // ══════════════════════════════════════════════════════════════════════
    // STOP SIGNAL HELPERS
    // ══════════════════════════════════════════════════════════════════════

    private async Task<bool> IsBotStoppingAsync(int botId)
    {
        var status = await _context.Bots
            .AsNoTracking()
            .Where(b => b.Id == botId)
            .Select(b => b.Status)
            .FirstOrDefaultAsync();

        return status == "stopping";
    }

    private async Task HandleStopAsync(Bot bot, int newCount)
    {
        await _botLogService.LogWarningAsync(bot.Id, bot.Name,
            $"🛑 Bot detenido manualmente. Propiedades procesadas en esta sesión: {newCount}");

        bot.Status = "stopped";
        bot.UpdatedAt = DateTime.UtcNow;
        bot.LastError = null;
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    // MAIN SCRAPING METHOD
    // ══════════════════════════════════════════════════════════════════════

    public async Task<List<Property>> ScrapePropertiesAsync(Bot bot)
    {
        var scrapedProperties = new List<Property>();

        try
        {
            _botLogService.ClearBotLogs(bot.Id);

            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🚀 Bot execution started");

            // Guard contra doble ejecucion (scheduler + manual simultaneos)
            await _context.Entry(bot).ReloadAsync();
            if (bot.Status == "running" || bot.Status == "stopping")
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Bot ya en ejecucion (status={bot.Status}). Cancelando instancia duplicada.");
                return scrapedProperties;
            }

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

            if (await IsBotStoppingAsync(bot.Id))
            {
                await HandleStopAsync(bot, 0);
                return scrapedProperties;
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 2: Extraer datos embebidos en JSON
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "📦 Extracting embedded JSON data (Next.js, JSON-LD, __PRELOADED_STATE__, etc.)");
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

            if (await IsBotStoppingAsync(bot.Id))
            {
                await HandleStopAsync(bot, 0);
                return scrapedProperties;
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 3: Pipeline de limpieza HTML (SIMPLIFICADO)
            // Solo eliminamos tags que NUNCA tienen contenido útil y
            // convertimos a texto. Sin scoring ni extracción selectiva
            // para evitar perder datos en SPAs.
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🧹 Cleaning HTML (removing scripts, styles, SVGs…)");
            var cleanedHtml = RemoveNoiseTags(html);
            var reductionPct = html.Length > 0 ? 100 - (cleanedHtml.Length * 100 / html.Length) : 0;
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ Noise removed: {cleanedHtml.Length:N0} chars (reduced {reductionPct}%)");

            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "📝 Converting HTML to compact text");
            var compactText = ConvertHtmlToText(cleanedHtml);
            await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                $"📝 Compact text from HTML: {compactText.Length:N0} chars");

            // ══════════════════════════════════════════════════════════════
            // FASE 4: Combinar texto HTML + datos embebidos
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
                    // Poner embedded data primero: suele ser más estructurado y útil
                    compactText = embeddedData + "\n\n--- HTML TEXT ---\n\n" + compactText;
                }
            }

            if (await IsBotStoppingAsync(bot.Id))
            {
                await HandleStopAsync(bot, 0);
                return scrapedProperties;
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 5: Fallback a Playwright si el contenido es escaso
            // ══════════════════════════════════════════════════════════════
            if (compactText.Length < 2_000 && html.Length > 10_000)
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Sparse text ({compactText.Length} chars) — retrying with headless browser…");
                try
                {
                    var (jsHtml, capturedApiData) = await DownloadHtmlWithPlaywrightAsync(bot.Url);

                    if (await IsBotStoppingAsync(bot.Id))
                    {
                        await HandleStopAsync(bot, 0);
                        return scrapedProperties;
                    }

                    var jsEmbedded  = ExtractEmbeddedJsonData(jsHtml);
                    var jsCleaned   = RemoveNoiseTags(jsHtml);
                    var jsText      = ConvertHtmlToText(jsCleaned);

                    await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                        $"📦 Playwright results — HTML text: {jsText.Length:N0} chars | Embedded: {jsEmbedded.Length:N0} chars | API data: {capturedApiData.Length:N0} chars");

                    // Combinar todas las fuentes de Playwright
                    var combined = new StringBuilder();
                    if (capturedApiData.Length > 100) combined.AppendLine(capturedApiData);
                    if (jsEmbedded.Length > 100)      combined.AppendLine(jsEmbedded);
                    if (jsText.Length > 0)             combined.AppendLine(jsText);

                    var playwrightResult = combined.ToString().Trim();

                    if (playwrightResult.Length > compactText.Length)
                    {
                        compactText = playwrightResult;
                        await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                            $"✅ Headless browser extracted: {compactText.Length:N0} chars");
                    }
                    else
                    {
                        await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                            $"⚠️ Headless browser also sparse ({playwrightResult.Length} chars)");
                    }
                }
                catch (Exception ex)
                {
                    await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                        $"⚠️ Headless browser failed: {ex.Message}");
                }
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 6: Verificar contenido mínimo
            // ══════════════════════════════════════════════════════════════
            if (compactText.Length < 200)
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    "⚠️ No se pudo extraer texto legible suficiente. Abortando.");
                bot.Status = "completed";
                bot.LastRunCount = 0;
                bot.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                    "🎉 Bot completed. Scraped: 0 | New: 0 | Total: " + bot.TotalScraped);
                return scrapedProperties;
            }

            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ Compact text ready: {compactText.Length:N0} characters");

            if (await IsBotStoppingAsync(bot.Id))
            {
                await HandleStopAsync(bot, 0);
                return scrapedProperties;
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 7: Enviar a Bedrock
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🤖 Sending to AWS Bedrock (Claude AI) for processing");
            scrapedProperties = await ExtractPropertiesWithBedrock(compactText, bot);

            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"✅ AI processing completed: {scrapedProperties.Count} properties found");

            if (await IsBotStoppingAsync(bot.Id))
            {
                await HandleStopAsync(bot, 0);
                return scrapedProperties;
            }

            // ══════════════════════════════════════════════════════════════
            // FASE 8: Upsert + Snapshot por cada propiedad
            // ══════════════════════════════════════════════════════════════
            await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                "💾 Processing properties (upsert + snapshots)...");

            int newCount = 0;
            int updatedCount = 0;
            int unchangedCount = 0;

            for (int i = 0; i < scrapedProperties.Count; i++)
            {
                if (i % 5 == 0 && await IsBotStoppingAsync(bot.Id))
                {
                    await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                        $"⚠️ Stop signal at {i}/{scrapedProperties.Count}.");
                    break;
                }

                var property = scrapedProperties[i];

                await _botLogService.SendProgressAsync(bot.Id, bot.Name, i + 1, scrapedProperties.Count,
                    $"Processing: {property.Title?.Substring(0, Math.Min(40, property.Title?.Length ?? 0))}...");

                if (!string.IsNullOrWhiteSpace(property.SourceUrl))
                {
                    var scraped = property.SourceUrl.Trim().TrimEnd('/');
                    var botUrl  = bot.Url.Trim().TrimEnd('/');
                    if (string.Equals(scraped, botUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        property.SourceUrl = null;
                    }
                }

                var result = await _upsertService.UpsertPropertyAsync(property, bot.Id);

                switch (result)
                {
                    case UpsertResult.New:
                        newCount++;
                        await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                            $"➕ Nueva: {property.Title}");
                        break;

                    case UpsertResult.Updated:
                        updatedCount++;
                        await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                            $"📝 Actualizada: {property.Title}");
                        break;

                    case UpsertResult.Unchanged:
                        unchangedCount++;
                        break;
                }
            }

            await _context.SaveChangesAsync();

            await _botLogService.LogSuccessAsync(bot.Id, bot.Name,
                $"💾 Resultado: {newCount} nuevas | {updatedCount} actualizadas | {unchangedCount} sin cambios " +
                $"({newCount + updatedCount + unchangedCount} snapshots creados)");

            // ══════════════════════════════════════════════════════════════
            // FASE 9: Actualizar estado final del bot
            // ══════════════════════════════════════════════════════════════
            await _context.Entry(bot).ReloadAsync();
            var wasStopped = bot.Status == "stopping";

            bot.Status       = wasStopped ? "stopped" : "completed";
            bot.LastRunCount = scrapedProperties.Count;
            bot.TotalScraped += newCount;
            bot.UpdatedAt    = DateTime.UtcNow;
            bot.LastError    = null;
            await _context.SaveChangesAsync();

            var finalMsg = wasStopped
                ? $"🛑 Detenido. Nuevas: {newCount} | Actualizadas: {updatedCount} | Total acumulado: {bot.TotalScraped}"
                : $"🎉 Completado! Nuevas: {newCount} | Actualizadas: {updatedCount} | Sin cambios: {unchangedCount} | Total: {bot.TotalScraped}";

            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, finalMsg);
            _logger.LogInformation("Bot {BotId} finished '{Status}'. New: {New}, Updated: {Updated}, Unchanged: {Unchanged}",
                bot.Id, bot.Status, newCount, updatedCount, unchangedCount);
        }
        catch (Exception ex)
        {
            await _botLogService.LogErrorAsync(bot.Id, bot.Name,
                $"❌ Bot execution failed: {ex.Message}", ex);

            bot.Status    = "error";
            bot.LastError = ex.Message;
            bot.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogError(ex, "Error in bot {BotId}", bot.Id);
            throw;
        }

        return scrapedProperties;
    }

    // ══════════════════════════════════════════════════════════════════════
    // EXTRACCIÓN DE DATOS EMBEBIDOS EN JSON
    // ══════════════════════════════════════════════════════════════════════

    // Regex más robusto: greedy capture del JSON completo.
    // Usa balanceo simple: captura desde { hasta el último } de la línea.
    private static readonly Regex _windowAssignmentRegex = new(
        @"window\[?['""]?__?\w+['""]?\]?\s*=\s*(\{.+\})\s*;?\s*$|window\[?['""]?__?\w+['""]?\]?\s*=\s*(\[.+\])\s*;?\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Captura self.__next_f.push([...]) usado por Next.js App Router
    private static readonly Regex _nextFPushRegex = new(
        @"self\.__next_f\.push\(\s*\[.*?""(.+?)""\s*\]\s*\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

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
            var scriptId   = script.GetAttributeValue("id",   "").ToLower();
            var content    = script.InnerText.Trim();

            if (string.IsNullOrEmpty(content) || content.Length < 50)
                continue;

            // 1. Scripts con tipo de dato explícito (JSON-LD, JSON inline)
            bool isTypedDataScript =
                scriptType == "application/ld+json" ||
                scriptType == "application/json" ||
                scriptId.Contains("__next_data__") ||
                scriptId.Contains("__nuxt");

            if (isTypedDataScript)
            {
                TryAppendJsonContent(content, sb);
                continue;
            }

            // 2. window.X = { ... } con JSON sustancial (greedy)
            var windowMatches = _windowAssignmentRegex.Matches(content);
            foreach (Match match in windowMatches)
            {
                var jsonCandidate = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
                if (jsonCandidate.Length > 200)
                    TryAppendJsonContent(jsonCandidate, sb);
            }

            // 3. Next.js App Router: self.__next_f.push([...])
            if (windowMatches.Count == 0)
            {
                var nextFMatches = _nextFPushRegex.Matches(content);
                foreach (Match match in nextFMatches)
                {
                    var payload = match.Groups[1].Value
                        .Replace("\\\"", "\"")
                        .Replace("\\\\", "\\");
                    if (payload.Length > 200)
                        TryAppendJsonContent(payload, sb);
                }
            }

            // 4. Fallback: scripts que empiezan directo con JSON
            if (windowMatches.Count == 0 && (content.StartsWith("{") || content.StartsWith("[")))
            {
                if (content.Length > 200)
                    TryAppendJsonContent(content, sb);
            }
        }

        return sb.ToString();
    }

    private static void TryAppendJsonContent(string content, StringBuilder sb)
    {
        var jsonContent = content.Replace("<!--", "").Replace("-->", "").Trim().TrimEnd(';');

        try
        {
            using var jsonDoc = JsonDocument.Parse(jsonContent);
            var extracted = ExtractTextFromJsonRecursive(jsonDoc.RootElement, maxDepth: 15);

            if (extracted.Length > 50)
            {
                sb.AppendLine("--- EMBEDDED DATA ---");
                sb.AppendLine(extracted);
                sb.AppendLine("--- END ---");
            }
        }
        catch (JsonException) { /* JSON malformado, ignorar */ }
    }

    private static string ExtractTextFromJsonRecursive(JsonElement element, int maxDepth, int currentDepth = 0)
    {
        if (currentDepth > maxDepth) return string.Empty;

        var sb = new StringBuilder();

        var skipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "buildid", "assetprefix", "scriptloader", "gsp", "gssp",
            "isfallback", "dynamicids", "customserver", "appgip",
            "__n_ssp", "runtimeconfig", "locale", "locales",
            "defaultlocale", "domainlocales", "icon", "favicon",
            "stylesheet", "chunks", "webpack",
            "namedchunkgroups", "hash", "contenthash", "entry"
        };

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (skipKeys.Contains(prop.Name)) continue;

                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var strVal = prop.Value.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(strVal) && strVal.Length > 1
                            && strVal.Length < 10000 && !IsAssetUrl(strVal))
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
                if (currentDepth >= 2 && currentDepth <= 8 && sb.Length > 50)
                    sb.AppendLine("---");
                break;

            case JsonValueKind.Array:
                int itemCount = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (itemCount++ > 500) break;
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
        if (string.IsNullOrEmpty(value) || value.Length < 10) return false;

        if (value.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)) return true;

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
    // HTML LIMPIEZA (SIMPLIFICADA)
    // Solo eliminamos tags que NUNCA contienen datos de propiedades.
    // NO hacemos scoring ni extracción selectiva — dejamos que el LLM
    // se encargue de encontrar las propiedades en el texto completo.
    // ══════════════════════════════════════════════════════════════════════

    private static readonly HashSet<string> _removeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "svg", "path", "noscript",
        "meta", "link", "iframe", "canvas",
        "video", "audio", "source", "track", "map", "area", "template",
        "head"
    };

    private static readonly HashSet<string> _blockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "p", "h1", "h2", "h3", "h4", "h5", "h6",
        "li", "ul", "ol", "article", "section", "main",
        "tr", "td", "th", "blockquote", "pre", "br", "hr",
        "header", "footer", "nav", "figure", "figcaption"
    };

    /// <summary>
    /// Paso 1: Elimina tags de ruido (scripts, styles, SVGs, head, etc.)
    /// NO elimina nav/header/footer porque en SPAs pueden contener listings.
    /// </summary>
    private static string RemoveNoiseTags(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        doc.DocumentNode.Descendants()
            .Where(n => _removeTags.Contains(n.Name))
            .ToList()
            .ForEach(n => n.Remove());

        // Eliminar comentarios HTML
        doc.DocumentNode.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Comment)
            .ToList()
            .ForEach(n => n.Remove());

        return doc.DocumentNode.OuterHtml;
    }

    /// <summary>
    /// Paso 2: Convierte HTML limpio a texto compacto.
    /// Preserva hrefs de links y atributos data-* que contienen datos.
    /// Preserva también atributos aria-label, title, alt que pueden
    /// contener texto de propiedades en SPAs.
    /// </summary>
    private static string ConvertHtmlToText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var sb = new StringBuilder();
        WalkNode(doc.DocumentNode, sb);

        // Limpiar whitespace redundante
        var text = Regex.Replace(sb.ToString(), @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 1);

        return string.Join("\n", lines).Trim();
    }

    private static void WalkNode(HtmlNode node, StringBuilder sb)
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

        // Extraer texto de atributos que SPAs usan para renderizar contenido
        var meaningfulAttrs = new[] { "aria-label", "title", "alt", "placeholder", "content" };
        foreach (var attrName in meaningfulAttrs)
        {
            var attrVal = node.GetAttributeValue(attrName, "").Trim();
            if (!string.IsNullOrEmpty(attrVal) && attrVal.Length > 2 && attrVal.Length < 500)
            {
                sb.Append($" {attrVal} ");
            }
        }

        // Extraer data-* attributes (contienen datos estructurados)
        foreach (var attr in node.Attributes)
        {
            if (attr.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(attr.Value)
                && attr.Value.Length > 1
                && attr.Value.Length < 500
                && !attr.Name.Contains("testid", StringComparison.OrdinalIgnoreCase)
                && !attr.Name.Contains("tracking", StringComparison.OrdinalIgnoreCase)
                && !attr.Name.Contains("analytics", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($" [{attr.Name}={attr.Value}] ");
            }
        }

        // Preservar href de links
        string? href = null;
        if (tag == "a")
        {
            href = node.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrEmpty(href) || href.StartsWith("javascript:") || href == "#")
                href = null;
        }

        foreach (var child in node.ChildNodes)
            WalkNode(child, sb);

        if (href != null)
            sb.Append($" [link:{href}] ");

        if (isBlock) sb.AppendLine();
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
            return html;

        await _botLogService.LogWarningAsync(bot.Id, bot.Name,
            "Detected JavaScript challenge page; retrying with headless browser.");

        try
        {
            var (renderedHtml, _) = await DownloadHtmlWithPlaywrightAsync(bot.Url);
            if (!string.IsNullOrWhiteSpace(renderedHtml))
                return renderedHtml;
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
        if (string.IsNullOrWhiteSpace(html)) return false;

        return html.Contains("requires JavaScript", StringComparison.OrdinalIgnoreCase)
            || html.Contains("_bmstate",            StringComparison.OrdinalIgnoreCase)
            || html.Contains("verifyChallenge",     StringComparison.OrdinalIgnoreCase)
            || html.Contains("window.location.reload()", StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════
    // PLAYWRIGHT
    // ══════════════════════════════════════════════════════════════════════

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
            UserAgent    = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36",
            Locale       = "es-CL",
            TimezoneId   = "America/Santiago",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            JavaScriptEnabled = true
        });

        var capturedJsonResponses = new List<string>();
        var page = await context.NewPageAsync();

        // Capturar TODAS las respuestas de API (JSON, texto con datos, etc.)
        page.Response += async (_, response) =>
        {
            try
            {
                var responseUrl = response.Url;
                var contentType = response.Headers.GetValueOrDefault("content-type", "");
                var status      = response.Status;

                // DEBUG TEMPORAL: loguear TODAS las respuestas para identificar la API de listings
                var ctShort = contentType.Length > 30 ? contentType[..30] : contentType;
                var urlShort = responseUrl.Length > 150 ? responseUrl[..150] : responseUrl;
                Console.WriteLine($"[PW-DEBUG] {status} | {ctShort} | {urlShort}");

                // Filtrar assets estáticos
                bool isStaticAsset =
                       responseUrl.Contains(".js", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains(".css", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains(".woff", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains(".png", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains(".svg", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains(".gif", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains(".ico", StringComparison.OrdinalIgnoreCase);

                bool isTracker =
                       responseUrl.Contains("analytics", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains("tracking", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains("google", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains("facebook", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains("hotjar", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains("sentry", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains("newrelic", StringComparison.OrdinalIgnoreCase)
                    || responseUrl.Contains("datadog", StringComparison.OrdinalIgnoreCase);

                bool isDataResponse = contentType.Contains("json")
                    || contentType.Contains("text/plain")
                    || contentType.Contains("text/html");

                if (!isStaticAsset && !isTracker && isDataResponse && status >= 200 && status < 300)
                {
                    var body = await response.TextAsync();
                    Console.WriteLine($"[PW-CAPTURE] Candidate: {urlShort} | bodyLen={body.Length} | startsJSON={body.TrimStart().StartsWith("{") || body.TrimStart().StartsWith("[")}");
                    // Umbral bajo: capturar cualquier respuesta con datos
                    if (body.Length > 100 && (body.TrimStart().StartsWith("{") || body.TrimStart().StartsWith("[")))
                    {
                        capturedJsonResponses.Add(body);
                        Console.WriteLine($"[PW-CAPTURED] ✅ {urlShort} | {body.Length} chars");
                    }
                }
                else if (!isStaticAsset && status >= 200 && status < 300)
                {
                    Console.WriteLine($"[PW-SKIPPED] reason={( isTracker ? "tracker" : "not-data-content-type" )} | {urlShort}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Response capture skipped ({response.Url}): {ex.Message}");
            }
        };

        // Stealth + interceptor de fetch/XHR para capturar response bodies
        // Playwright no siempre puede re-leer el body con response.TextAsync()
        // así que capturamos desde JS antes de que la página consuma los datos.
        await context.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            Object.defineProperty(navigator, 'plugins',   { get: () => [1,2,3,4,5] });
            Object.defineProperty(navigator, 'languages', { get: () => ['es-CL','es','en'] });
            window.chrome = { runtime: {} };

            // Almacén global de respuestas capturadas
            window.__capturedResponses = [];

            // Interceptar fetch()
            const origFetch = window.fetch;
            window.fetch = async function(...args) {
                const response = await origFetch.apply(this, args);
                try {
                    const url = (typeof args[0] === 'string') ? args[0] : args[0]?.url || '';
                    const ct = response.headers.get('content-type') || '';
                    // Solo capturar respuestas de datos, no assets
                    const isData = !url.match(/\.(js|css|png|jpg|gif|svg|woff|ico)(\?|$)/i);
                    const isTracker = /analytics|tracking|google|facebook|hotjar|sentry|clarity/i.test(url);
                    if (isData && !isTracker && response.ok) {
                        const clone = response.clone();
                        clone.text().then(body => {
                            if (body && body.length > 50) {
                                window.__capturedResponses.push({ url: url.substring(0, 200), body: body, ct: ct });
                            }
                        }).catch(() => {});
                    }
                } catch(e) {}
                return response;
            };

            // Interceptar XMLHttpRequest
            const origOpen = XMLHttpRequest.prototype.open;
            const origSend = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                this.__url = url;
                return origOpen.call(this, method, url, ...rest);
            };
            XMLHttpRequest.prototype.send = function(...args) {
                this.addEventListener('load', function() {
                    try {
                        const url = this.__url || '';
                        const isData = !url.match(/\.(js|css|png|jpg|gif|svg|woff|ico)(\?|$)/i);
                        const isTracker = /analytics|tracking|google|facebook|hotjar|sentry|clarity/i.test(url);
                        if (isData && !isTracker && this.status >= 200 && this.status < 300) {
                            const body = this.responseText;
                            if (body && body.length > 50) {
                                window.__capturedResponses.push({ url: url.substring(0, 200), body: body, ct: this.getResponseHeader('content-type') || '' });
                            }
                        }
                    } catch(e) {}
                });
                return origSend.apply(this, args);
            };
        ");

        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 45000
            });
        }
        catch (TimeoutException) { }

        // Esperar a que el body tenga texto suficiente (polling manual)
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var textLength = await page.EvaluateAsync<int>("document.body?.innerText?.length ?? 0");
                if (textLength > 1000) break;
            }
            catch { }

            await page.WaitForTimeoutAsync(500);
        }

        // Espera adicional para XHR tardíos
        await page.WaitForTimeoutAsync(3000);

        // Scroll para cargar lazy content
        try
        {
            await page.EvaluateAsync(@"
                async () => {
                    const delay = (ms) => new Promise(r => setTimeout(r, ms));
                    for (let i = 0; i < 15; i++) {
                        window.scrollBy(0, 400);
                        await delay(300);
                        if ((window.innerHeight + window.scrollY) >= document.body.scrollHeight - 100) break;
                    }
                    window.scrollTo(0, 0);
                    await delay(1000);
                }
            ");
        }
        catch { }

        // Espera post-scroll para capturar lazy-loaded API calls
        await page.WaitForTimeoutAsync(2000);

        // CLAVE: Leer el texto visible directamente del DOM renderizado.
        // Esto captura TODO lo que JS renderizó, sin depender de HTML parsing.
        var domInnerText = "";
        try
        {
            domInnerText = await page.EvaluateAsync<string>("document.body?.innerText ?? ''") ?? "";
            Console.WriteLine($"[PW-DOM] innerText length: {domInnerText.Length}");

            // Log de preview para debug
            if (domInnerText.Length > 0)
            {
                var preview = domInnerText.Length > 500 ? domInnerText[..500] : domInnerText;
                Console.WriteLine($"[PW-DOM] Preview: {preview}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PW-DOM] Error reading innerText: {ex.Message}");
        }

        var renderedHtml = await page.ContentAsync();

        // Leer las respuestas capturadas por el interceptor JS
        var jsCapturedData = new List<string>();
        try
        {
            var capturedCount = await page.EvaluateAsync<int>("window.__capturedResponses?.length ?? 0");
            Console.WriteLine($"[PW-JS-INTERCEPTOR] Captured {capturedCount} responses via JS hooks");

            for (int i = 0; i < capturedCount; i++)
            {
                try
                {
                    var entry = await page.EvaluateAsync<JsonElement>($"window.__capturedResponses[{i}]");
                    var entryUrl  = entry.GetProperty("url").GetString() ?? "";
                    var entryBody = entry.GetProperty("body").GetString() ?? "";
                    var entryCt   = entry.GetProperty("ct").GetString() ?? "";

                    Console.WriteLine($"[PW-JS-CAPTURED] {entryUrl} | ct={entryCt.Substring(0, Math.Min(30, entryCt.Length))} | bodyLen={entryBody.Length}");

                    if (entryBody.Length > 100)
                    {
                        jsCapturedData.Add(entryBody);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PW-JS-ERROR] Reading entry {i}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PW-JS-ERROR] Reading __capturedResponses: {ex.Message}");
        }

        // Combinar datos: Playwright response handler + JS interceptor
        var allCapturedJson = new List<string>(capturedJsonResponses);
        allCapturedJson.AddRange(jsCapturedData);

        var apiDataSb = new StringBuilder();
        foreach (var json in allCapturedJson)
        {
            try
            {
                // Intentar parsear como JSON
                var trimmed = json.Trim();
                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    using var jsonDoc = JsonDocument.Parse(trimmed);
                    var text = ExtractTextFromJsonRecursive(jsonDoc.RootElement, maxDepth: 15);
                    if (text.Length > 50)
                    {
                        apiDataSb.AppendLine("--- CAPTURED API DATA ---");
                        apiDataSb.AppendLine(text);
                        apiDataSb.AppendLine("--- END ---");
                    }
                }
                else if (trimmed.Length > 200)
                {
                    // No es JSON pero tiene contenido (HTML con datos?)
                    // Intentar extraer texto útil del HTML
                    var doc = new HtmlDocument();
                    doc.LoadHtml(trimmed);
                    var innerText = doc.DocumentNode.InnerText?.Trim();
                    if (!string.IsNullOrEmpty(innerText) && innerText.Length > 100)
                    {
                        apiDataSb.AppendLine("--- CAPTURED API HTML ---");
                        apiDataSb.AppendLine(innerText);
                        apiDataSb.AppendLine("--- END ---");
                    }
                }
            }
            catch { }
        }

        // Incluir el texto visible del DOM como fuente de datos
        if (domInnerText.Length > 200)
        {
            apiDataSb.Insert(0, "--- RENDERED PAGE TEXT ---\n" + domInnerText + "\n--- END ---\n\n");
        }

        return (renderedHtml, apiDataSb.ToString());
    }

    // ══════════════════════════════════════════════════════════════════════
    // BEDROCK: Extracción con LLM
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Divide el texto en chunks respetando separadores naturales.
    /// Chunk size más grande (40K) porque ya no pre-filtramos tan agresivamente
    /// y necesitamos darle más contexto al LLM.
    /// </summary>
    private static IEnumerable<string> ChunkText(string text, int maxChunkSize = 40_000)
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
                // Buscar un buen punto de corte (doble newline, luego newline simple)
                int breakPos = text.LastIndexOf("\n\n", end, Math.Min(5000, end - start));
                if (breakPos <= start + maxChunkSize / 2)
                    breakPos = text.LastIndexOf("\n", end, Math.Min(2000, end - start));
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

        var chunks = ChunkText(compactText, maxChunkSize: 40_000).ToList();
        await _botLogService.LogInfoAsync(bot.Id, bot.Name,
            $"📦 Content split into {chunks.Count} chunk(s) (model: {modelId})");

        var allProperties = new List<Property>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < chunks.Count; i++)
        {
            if (await IsBotStoppingAsync(bot.Id))
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Stop signal between chunks ({i}/{chunks.Count}).");
                break;
            }

            var preview = chunks[i].Length > 200 ? chunks[i][..200] + "…" : chunks[i];
            await _botLogService.LogInfoAsync(bot.Id, bot.Name,
                $"🤖 Processing chunk {i + 1}/{chunks.Count} ({chunks[i].Length:N0} chars)...\nPreview: {preview}");

            var chunkProperties = await ProcessChunkWithBedrock(chunks[i], bot, modelId);

            int added = 0;
        foreach (var prop in chunkProperties)
        {
         // Dedup key más robusto: normaliza título (lowercase, sin acentos,
        // sin puntuación, colapsar espacios) para que variaciones menores
        // del LLM entre chunks no generen duplicados.         
        // Ej: "Edificio en Las Condes" vs "Edificio Las Condes" → misma key.
            var normTitle = NormalizeForDedup(prop.Title);
            var normUrl   = (prop.SourceUrl ?? "").Trim().ToLowerInvariant();
            var normCity  = NormalizeForDedup(prop.City);
            var normType  = NormalizeForDedup(prop.PropertyType);

         // Key primaria: por URL (si existe)
         // Key secundaria: por título + ciudad + tipo (sin URL)
            var key = !string.IsNullOrEmpty(normUrl)
             ? $"url:{normUrl}"
             : $"text:{normTitle}|{normCity}|{normType}";

            if (seenKeys.Add(key))
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
El texto puede contener:
- URLs entre corchetes como [link:https://...]
- Datos estructurados con formato ""campo: valor"" extraídos de APIs o JSON embebido
- Atributos data-* entre corchetes como [data-price=5000]
- Texto de atributos aria-label, title, alt extraídos del HTML
- Secciones marcadas como --- EMBEDDED DATA --- o --- CAPTURED API DATA ---

El texto puede ser desordenado o repetitivo (viene de un scraper). Tu trabajo es identificar
las propiedades inmobiliarias únicas dentro del contenido.

Extrae TODAS las propiedades inmobiliarias que encuentres. Para cada una:
- title: Título o descripción de la propiedad
- sourceUrl: URL completa de la propiedad individual (busca en [link:...] o en campos como url, permalink, href). IMPORTANTE: debe ser la URL del detalle de la propiedad, NO la URL de la página de resultados/listados.
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
- publicationDate: Fecha de publicación del aviso (formato ISO: YYYY-MM-DD si está disponible)
- condition: Estado de la propiedad: ""Nuevo"" o ""Usado"" (si está indicado en el aviso)

Reglas:
- Si un campo no está presente, usa null (no inventes datos)
- sourceUrl debe ser la URL específica de la propiedad, no la URL general del sitio
- Si no encuentras una URL individual para una propiedad, deja sourceUrl como null
- price debe ser solo el número (ej: 150000000 para $150.000.000, o 4500 para UF 4.500)
- Incluye TODAS las propiedades que veas, no omitas ninguna
- Si no encuentras ninguna propiedad, responde con un array vacío

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
      ""description"": ""Amplio departamento con terraza"",
      ""publicationDate"": ""2026-01-15"",
      ""condition"": ""Usado""
    }}
  ]
}}

TEXTO:
{chunkText}";

        var request = new ConverseRequest
        {
            ModelId  = modelId,
            Messages = new List<Message>
            {
                new Message
                {
                    Role    = "user",
                    Content = new List<ContentBlock> { new ContentBlock { Text = prompt } }
                }
            },
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = 8000,
                Temperature = 0.1f
            }
        };

        const int maxRetries = 2;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response      = await _bedrockClient.ConverseAsync(request);
                var stopReason    = response?.StopReason ?? "unknown";
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
                    SourceUrl    = p.SourceUrl,
                    Price        = p.Price,
                    Currency     = p.Currency ?? "CLP",
                    Address      = p.Address,
                    City         = p.City,
                    Region       = p.Region,
                    Neighborhood = p.Neighborhood,
                    Bedrooms     = p.Bedrooms,
                    Bathrooms    = p.Bathrooms,
                    Area         = p.Area,
                    PropertyType    = p.PropertyType,
                    Description     = p.Description,
                    PublicationDate = p.PublicationDate,
                    Condition       = p.Condition
                }).ToList() ?? new List<Property>();
            }
            catch (JsonException ex)
            {
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Invalid JSON from Bedrock: {ex.Message}. Chunk skipped.");
                return new List<Property>();
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delay = 2000 * (attempt + 1);
                await _botLogService.LogWarningAsync(bot.Id, bot.Name,
                    $"⚠️ Bedrock error (attempt {attempt + 1}): {ex.Message}. Retrying in {delay}ms...");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                await _botLogService.LogErrorAsync(bot.Id, bot.Name,
                    $"❌ Bedrock API error ({ex.GetType().Name}): {ex.Message}", ex);
                return new List<Property>();
            }
        }

        return new List<Property>();
    }

    private static bool IsMockScrapingEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SCRAPER_MOCK_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static List<Property> BuildMockProperties(Bot bot) =>
        new List<Property>
        {
            new Property
            {
                Title        = $"Mock property for {bot.Name}",
                SourceUrl    = $"{bot.Url}/mock-property-1",
                Price        = 100000000,
                Currency     = "CLP",
                Address      = "Av. Providencia 123",
                City         = "Santiago",
                Region       = "Metropolitana",
                Neighborhood = "Providencia",
                Bedrooms     = 3,
                Bathrooms    = 2,
                Area         = 85,
                PropertyType    = "casa",
                Description     = "Mock data generated for testing.",
                PublicationDate = DateTime.UtcNow.AddDays(-10),
                Condition       = "Usado"
            }
        };

    /// <summary>
    /// Normaliza texto para deduplicación en memoria: lowercase, sin acentos,
    /// sin puntuación, espacios colapsados.
    /// </summary>
    private static string NormalizeForDedup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim().ToLowerInvariant();
        s = s.Replace("á", "a").Replace("é", "e").Replace("í", "i")
             .Replace("ó", "o").Replace("ú", "u").Replace("ñ", "n")
             .Replace("ü", "u");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^\w\s]", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    // ══════════════════════════════════════════════════════════════════════
    // DTOs internos para Bedrock
    // ══════════════════════════════════════════════════════════════════════

    private class BedrockResponse
    {
        public List<PropertyDto>? Properties { get; set; }
    }

    private class PropertyDto
    {
        public string?  Title        { get; set; }
        public string?  SourceUrl    { get; set; }
        public decimal? Price        { get; set; }
        public string?  Currency     { get; set; }
        public string?  Address      { get; set; }
        public string?  City         { get; set; }
        public string?  Region       { get; set; }
        public string?  Neighborhood { get; set; }
        public int?     Bedrooms     { get; set; }
        public int?     Bathrooms    { get; set; }
        public decimal? Area         { get; set; }
        public string?  PropertyType    { get; set; }
        public string?  Description     { get; set; }
        public DateTime? PublicationDate { get; set; }
        public string?  Condition       { get; set; }
    }
}