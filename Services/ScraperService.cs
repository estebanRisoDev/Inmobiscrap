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
            var client = _httpClientFactory.CreateClient();
            var html = await client.GetStringAsync(bot.Url);
            
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, $"✅ HTML downloaded: {html.Length:N0} characters");
            _logger.LogInformation($"Downloaded HTML: {html.Length} characters");

            // 2. Limpiar HTML
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🧹 Cleaning HTML (removing scripts, styles, etc.)");
            var cleanedHtml = CleanHtml(html);
            
            var reductionPercentage = 100 - (cleanedHtml.Length * 100 / html.Length);
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, 
                $"✅ HTML cleaned: {cleanedHtml.Length:N0} characters (reduced {reductionPercentage}%)");
            _logger.LogInformation($"Cleaned HTML: {cleanedHtml.Length} characters (reduced {reductionPercentage}%)");

            // 3. Extraer contenido relevante
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🔍 Extracting relevant content (listings section)");
            var relevantHtml = ExtractRelevantContent(cleanedHtml);
            
            await _botLogService.LogSuccessAsync(bot.Id, bot.Name, 
                $"✅ Relevant content extracted: {relevantHtml.Length:N0} characters");
            _logger.LogInformation($"Relevant HTML: {relevantHtml.Length} characters");

            // 4. Usar Bedrock para extraer propiedades
            await _botLogService.LogInfoAsync(bot.Id, bot.Name, "🤖 Sending HTML to AWS Bedrock (Claude AI) for processing");
            scrapedProperties = await ExtractPropertiesWithBedrock(relevantHtml, bot);
            
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
                
                // Verificar si ya existe (por título y dirección)
                var exists = await _context.Properties.AnyAsync(p => 
                    p.Title == property.Title && p.Address == property.Address);
                
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

    private string CleanHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remover scripts, styles, etc.
        doc.DocumentNode.Descendants()
            .Where(n => n.Name == "script" || n.Name == "style" || n.Name == "svg" || n.Name == "path")
            .ToList()
            .ForEach(n => n.Remove());

        return doc.DocumentNode.OuterHtml;
    }

    private string ExtractRelevantContent(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Buscar sección de listings (ajusta según tu sitio)
        var listingsSection = doc.DocumentNode.SelectSingleNode("//main") 
                           ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'listings')]")
                           ?? doc.DocumentNode;

        return listingsSection.OuterHtml;
    }

    private async Task<List<Property>> ExtractPropertiesWithBedrock(string html, Bot bot)
    {
        var prompt = $@"
Extrae todas las propiedades inmobiliarias del siguiente HTML.

Para cada propiedad, extrae:
- title: Título de la propiedad
- price: Precio (solo número, sin símbolos ni puntos)
- currency: Moneda (CLP, USD, UF, etc.) - por defecto CLP
- address: Dirección completa
- city: Ciudad
- region: Región
- neighborhood: Barrio/Comuna
- bedrooms: Número de dormitorios (número)
- bathrooms: Número de baños (número)
- area: Superficie en m² (número)
- propertyType: Tipo (departamento, casa, oficina, terreno, etc.)
- description: Descripción breve

Responde SOLO con un JSON válido con un array de propiedades:
{{
  ""properties"": [
    {{
      ""title"": ""Departamento en Las Condes"",
      ""price"": 150000000,
      ""currency"": ""CLP"",
      ""address"": ""Av. Apoquindo 1234"",
      ""city"": ""Santiago"",
      ""region"": ""Metropolitana"",
      ""neighborhood"": ""Las Condes"",
      ""bedrooms"": 3,
      ""bathrooms"": 2,
      ""area"": 120.5,
      ""propertyType"": ""departamento"",
      ""description"": ""Hermoso departamento con vista panorámica""
    }}
  ]
}}

HTML:
{html}
";

        var request = new ConverseRequest
        {
            ModelId = "us.anthropic.claude-3-5-sonnet-20241022-v2:0",
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
                MaxTokens = 4000,
                Temperature = 0.3f
            }
        };

        var response = await _bedrockClient.ConverseAsync(request);
        var jsonResponse = response.Output.Message.Content[0].Text;

        // Limpiar markdown si viene con ```json
        jsonResponse = jsonResponse.Replace("```json", "").Replace("```", "").Trim();

        var result = JsonSerializer.Deserialize<BedrockResponse>(jsonResponse, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return result?.Properties?.Select(p => new Property
        {
            Title = p.Title ?? string.Empty,
            Price = p.Price,
            Currency = p.Currency ?? "CLP",
            Address = p.Address,
            City = p.City,
            Region = p.Region,
            Neighborhood = p.Neighborhood,
            Bedrooms = p.Bedrooms,
            Bathrooms = p.Bathrooms,
            Area = p.Area,
            PropertyType = p.PropertyType,
            Description = p.Description
        }).ToList() ?? new List<Property>();
    }

    // Clases auxiliares para deserialización
    private class BedrockResponse
    {
        public List<PropertyDto>? Properties { get; set; }
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