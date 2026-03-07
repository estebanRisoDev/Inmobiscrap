using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Inmobiscrap.Services;

namespace Inmobiscrap.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportController : ControllerBase
{
    private readonly IReportService _reportService;
    private readonly ILogger<ReportController> _logger;

    public ReportController(IReportService reportService, ILogger<ReportController> logger)
    {
        _reportService = reportService;
        _logger        = logger;
    }

    /// <summary>
    /// GET /api/reports/market
    /// Genera y descarga un informe PDF del mercado inmobiliario.
    /// Acepta los mismos filtros que /api/analytics/market.
    /// </summary>
    [HttpGet("market")]
    public async Task<IActionResult> GetMarketReport(
        [FromQuery] string? region       = null,
        [FromQuery] string? city         = null,
        [FromQuery] string? neighborhood = null,
        [FromQuery] string? propertyType = null)
    {
        _logger.LogInformation(
            "User {User} requested market report — region={R} city={C} neighborhood={N} type={T}",
            User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown",
            region, city, neighborhood, propertyType);

        try
        {
            var filters = new ReportFilters(region, city, neighborhood, propertyType);
            var pdfBytes = await _reportService.GenerateMarketReportAsync(filters);

            var fileName = BuildFileName(filters);

            return File(
                pdfBytes,
                "application/pdf",
                fileName
            );
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Report generation failed");
            return StatusCode(500, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating report");
            return StatusCode(500, new { message = "Error inesperado al generar el informe." });
        }
    }

    private static string BuildFileName(ReportFilters f)
    {
        var parts = new List<string> { "informe-inmobiliario" };
        if (!string.IsNullOrWhiteSpace(f.City))         parts.Add(Slug(f.City!));
        else if (!string.IsNullOrWhiteSpace(f.Region))  parts.Add(Slug(f.Region!));
        if (!string.IsNullOrWhiteSpace(f.PropertyType)) parts.Add(Slug(f.PropertyType!));
        parts.Add(DateTime.Now.ToString("yyyyMMdd"));
        return string.Join("-", parts) + ".pdf";
    }

    private static string Slug(string s) =>
        s.ToLower()
         .Replace(" ", "-")
         .Replace("á","a").Replace("é","e").Replace("í","i")
         .Replace("ó","o").Replace("ú","u").Replace("ñ","n");
}