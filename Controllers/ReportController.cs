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
    // Mismo costo que las vistas equivalentes del dashboard
    private const int MarketReportCost       = 5;
    private const int ComparisonCostPerItem  = 5;

    private readonly IReportService _reportService;
    private readonly ICreditService _credits;
    private readonly ILogger<ReportController> _logger;

    public ReportController(IReportService reportService, ICreditService credits, ILogger<ReportController> logger)
    {
        _reportService = reportService;
        _credits       = credits;
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

        var filters = new ReportFilters(region, city, neighborhood, propertyType);
        return await GenerateWithCreditsAsync(
            MarketReportCost,
            () => _reportService.GenerateMarketReportAsync(filters),
            BuildFileName(filters),
            "Error inesperado al generar el informe.");
    }

    /// <summary>
    /// GET /api/reports/compare?ids=1,2,3
    /// Genera un PDF comparativo de propiedades específicas.
    /// </summary>
    [HttpGet("compare")]
    public async Task<IActionResult> GetComparisonReport([FromQuery] string ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return BadRequest(new { message = "Proporciona IDs de propiedades separados por coma." });

        var idList = ids.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(20)
            .ToList();

        if (idList.Count < 2)
            return BadRequest(new { message = "Se necesitan al menos 2 propiedades para comparar." });

        _logger.LogInformation(
            "User {User} requested comparison report — ids={Ids}",
            User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown",
            string.Join(",", idList));

        return await GenerateWithCreditsAsync(
            idList.Count * ComparisonCostPerItem,
            () => _reportService.GenerateComparisonReportAsync(idList),
            $"comparativa-{idList.Count}-propiedades-{DateTime.Now:yyyyMMdd}.pdf",
            "Error inesperado al generar el informe comparativo.");
    }

    /// <summary>
    /// Cobra créditos, genera el PDF y reembolsa si la generación falla.
    /// </summary>
    private async Task<IActionResult> GenerateWithCreditsAsync(
        int cost, Func<Task<byte[]>> generate, string fileName, string unexpectedErrorMessage)
    {
        var userId = this.CurrentUserId();
        var charge = await _credits.TryConsumeAsync(userId, cost);
        var error  = this.CreditErrorOrNull(charge);
        if (error != null) return error;

        try
        {
            var pdfBytes = await generate();
            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Report generation failed");
            await RefundIfChargedAsync(userId, charge);
            return StatusCode(500, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating report");
            await RefundIfChargedAsync(userId, charge);
            return StatusCode(500, new { message = unexpectedErrorMessage });
        }
    }

    private async Task RefundIfChargedAsync(int userId, CreditCheckResult charge)
    {
        if (charge.Status != CreditCheckStatus.Charged) return;
        try { await _credits.RefundAsync(userId, charge.Amount); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudieron reembolsar {Amount} créditos al usuario {UserId}", charge.Amount, userId); }
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
