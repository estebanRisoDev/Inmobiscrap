using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Inmobiscrap.Services;

namespace Inmobiscrap.Controllers;

public static class ControllerExtensions
{
    public static int CurrentUserId(this ControllerBase controller)
    {
        var claim = controller.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : 0;
    }

    /// <summary>
    /// Convierte el resultado del cobro de créditos en la respuesta HTTP de error
    /// (401/402), o null si se puede continuar. Agrega X-Credits-Remaining si cobró.
    /// </summary>
    public static IActionResult? CreditErrorOrNull(this ControllerBase controller, CreditCheckResult result)
    {
        switch (result.Status)
        {
            case CreditCheckStatus.UserNotFound:
                return controller.Unauthorized();

            case CreditCheckStatus.Insufficient:
                return controller.StatusCode(402, new
                {
                    message = $"Créditos insuficientes. Necesitas {result.Amount}, tienes {result.CreditsBefore}.",
                    credits = result.CreditsBefore,
                    plan    = result.Plan,
                });

            case CreditCheckStatus.Charged:
                controller.Response.Headers.Append("X-Credits-Remaining", result.Remaining.ToString());
                return null;

            default:
                return null;
        }
    }
}
