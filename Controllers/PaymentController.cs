using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;
using Inmobiscrap.Infrastructure;
using Inmobiscrap.Models;

namespace Inmobiscrap.Controllers;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record CreateCheckoutRequest(string PlanId);

// ── Controller ────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PaymentsController> _logger;

    // pack purchases use CheckoutPro (preferences), "pro" uses preapproval
    private static readonly Dictionary<string, (string Name, int AmountCLP, int Credits, bool IsPro)> Plans = new()
    {
        ["pack100"]  = ("Pack 100 créditos",  2500, 100, false),
        ["pro"]      = ("Plan Premium mensual", 5000,   0, true),   // CLP mensual
    };

    private static int ProAmountCLP => Plans["pro"].AmountCLP;

    public PaymentsController(
        ApplicationDbContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<PaymentsController> logger)
    {
        _context           = context;
        _configuration     = configuration;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    private int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : 0;
    }

    private string GetAccessToken() =>
        EnvConfig.Get("MP_ACCESS_TOKEN")
        ?? _configuration["MercadoPago:AccessToken"]
        ?? throw new InvalidOperationException("MP_ACCESS_TOKEN no configurado.");

    private bool IsSandbox() => GetAccessToken().StartsWith("TEST-");

    private string FrontendUrl() =>
        (EnvConfig.Get("FRONTEND_URL")
        ?? _configuration["MercadoPago:FrontendUrl"]
        ?? "http://localhost:3000").TrimEnd('/');

    private string BackendUrl() =>
        (EnvConfig.Get("BACKEND_URL")
        ?? _configuration["MercadoPago:BackendUrl"]
        ?? "http://localhost:5000").TrimEnd('/');

    private HttpClient CreateMpClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {GetAccessToken()}");
        return client;
    }

    /// <summary>
    /// Bloquea la fila del usuario (SELECT ... FOR UPDATE) dentro de la transacción
    /// actual. Serializa webhook + verify concurrentes del mismo usuario para que
    /// un pago no se aplique dos veces.
    /// </summary>
    private async Task<User?> LockUserAsync(int userId)
    {
        var users = await _context.Users
            .FromSqlInterpolated($@"SELECT * FROM ""Users"" WHERE ""Id"" = {userId} FOR UPDATE")
            .ToListAsync();
        var user = users.FirstOrDefault();

        // Si la entidad ya estaba trackeada, EF devuelve la instancia existente con
        // valores viejos: recargar ahora que tenemos el lock.
        if (user != null)
            await _context.Entry(user).ReloadAsync();
        return user;
    }

    // ── POST /api/payments/create-checkout ────────────────────────────────────
    // For packs → creates a CheckoutPro preference (one-time payment)
    // For "pro" → creates a Preapproval (recurring monthly subscription)
    [HttpPost("create-checkout")]
    [Authorize]
    public async Task<IActionResult> CreateCheckout([FromBody] CreateCheckoutRequest req)
    {
        var userId = GetUserId();
        var user   = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.PlanId) || !Plans.TryGetValue(req.PlanId, out var plan))
            return BadRequest(new { message = $"Plan '{req.PlanId}' no válido." });

        if (user.Plan == "pro" && !plan.IsPro)
            return BadRequest(new { message = "Ya tienes plan Pro con créditos ilimitados." });

        if (user.Plan == "pro" && plan.IsPro)
            return BadRequest(new { message = "Ya tienes una suscripción Pro activa." });

        return plan.IsPro
            ? await CreatePreapproval(user, plan)
            : await CreatePreference(user, plan, req.PlanId);
    }

    // ── POST /api/payments/webhook ────────────────────────────────────────────
    // No se confía en el contenido del webhook: solo se usa para saber QUÉ id
    // consultar; el estado real siempre se lee desde la API de MercadoPago.
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromQuery] string? type, [FromQuery] string? id)
    {
        string body;
        using (var reader = new StreamReader(HttpContext.Request.Body))
            body = await reader.ReadToEndAsync();

        _logger.LogInformation(
            "MP webhook raw — queryType={Type} queryId={Id} body={Body}",
            type, id, body.Length > 600 ? body[..600] : body);

        var notifType = type;
        var notifId   = id ?? Request.Query["data.id"].FirstOrDefault();

        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var json = JsonDocument.Parse(body).RootElement;

                // IPN format: {"topic":"payment","resource":"12345"}
                if (json.TryGetProperty("topic", out var topicProp))
                {
                    var topic = topicProp.GetString();

                    if (topic == "subscription_preapproval")
                    {
                        notifType = "subscription_preapproval";
                        if (string.IsNullOrEmpty(notifId) && json.TryGetProperty("resource", out var res))
                            notifId = ExtractIdFromResource(res.GetString() ?? "");
                    }
                    else if (topic == "payment")
                    {
                        notifType = "payment";
                        if (string.IsNullOrEmpty(notifId) && json.TryGetProperty("resource", out var res2))
                            notifId = ExtractIdFromResource(res2.GetString() ?? "");
                    }
                    else
                    {
                        _logger.LogInformation("MP webhook: topic {Topic} ignorado", topic);
                        return Ok();
                    }
                }
                else
                {
                    // Webhooks v2 format: {"type":"payment","data":{"id":"..."}}
                    if (string.IsNullOrEmpty(notifType) && json.TryGetProperty("type", out var t))
                        notifType = t.GetString();

                    if (string.IsNullOrEmpty(notifId) && json.TryGetProperty("data", out var data)
                        && data.TryGetProperty("id", out var dataId))
                        notifId = dataId.ValueKind == JsonValueKind.Number
                            ? dataId.GetInt64().ToString()
                            : dataId.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MP webhook: error parsing body");
            }
        }

        _logger.LogInformation("MP webhook resolved — type={Type} id={Id}", notifType, notifId);

        if (string.IsNullOrEmpty(notifId))
            return Ok();

        try
        {
            if (notifType == "subscription_preapproval")
            {
                await HandlePreapprovalWebhook(notifId);
            }
            else if (notifType == "payment")
            {
                var result = await VerifyAndApplyPaymentAsync(notifId, expectedUserId: null);
                _logger.LogInformation("MP webhook processed: paymentId={Id} result={Result}", notifId, result);
            }
            else
            {
                _logger.LogInformation("MP webhook ignored: type={Type}", notifType);
            }
        }
        catch (Exception ex)
        {
            // 500 → MercadoPago reintenta. El procesamiento es idempotente.
            _logger.LogError(ex, "MP webhook: error procesando {Type} {Id}", notifType, notifId);
            return StatusCode(500);
        }

        return Ok();
    }

    // ── GET /api/payments/verify/{paymentId} ──────────────────────────────────
    [HttpGet("verify/{paymentId}")]
    [Authorize]
    public async Task<IActionResult> VerifyPayment(string paymentId)
    {
        var userId = GetUserId();
        var status = await VerifyAndApplyPaymentAsync(paymentId, userId);
        return Ok(new { status, paymentId });
    }

    // ── GET /api/payments/verify-subscription/{preapprovalId} ─────────────────
    [HttpGet("verify-subscription/{preapprovalId}")]
    [Authorize]
    public async Task<IActionResult> VerifySubscription(string preapprovalId)
    {
        var userId = GetUserId();
        var user   = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        var (status, preapproval) = await FetchPreapproval(preapprovalId);

        if (preapproval == null)
            return BadRequest(new { message = "No se pudo verificar la suscripción.", status });

        // La suscripción debe pertenecer al usuario que la verifica
        if (ExtractUserIdFromReference(preapproval.Value) != userId)
        {
            _logger.LogWarning(
                "verify-subscription: userId={UserId} intentó verificar preapproval ajeno {Id}",
                userId, preapprovalId);
            return StatusCode(403, new { message = "Esta suscripción no pertenece a tu cuenta." });
        }

        _logger.LogInformation(
            "verify-subscription: userId={UserId} preapprovalId={Id} status={Status}",
            userId, preapprovalId, status);

        // Solo "authorized" significa que MP aprobó el cobro. "pending" se crea
        // apenas se genera el checkout (sin pagar), así que NO activa Pro: el
        // webhook lo activará cuando pase a "authorized" y el frontend hace polling.
        if (status == "authorized")
        {
            await ActivateProSubscription(userId, preapprovalId, preapproval.Value);
            return Ok(new { status = "approved", plan = "pro" });
        }

        return Ok(new { status, plan = user.Plan });
    }

    // ── GET /api/payments/history ─────────────────────────────────────────────
    [HttpGet("history")]
    [Authorize]
    public async Task<IActionResult> GetHistory()
    {
        var userId = GetUserId();
        var payments = await _context.Payments
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(50)
            .Select(p => new
            {
                p.Id,
                p.Type,
                p.Description,
                p.AmountCLP,
                p.Credits,
                p.MpId,
                p.CreatedAt,
            })
            .ToListAsync();

        return Ok(payments);
    }

    // ── GET /api/payments/subscription-status ─────────────────────────────────
    [HttpGet("subscription-status")]
    [Authorize]
    public async Task<IActionResult> SubscriptionStatus()
    {
        var userId = GetUserId();
        var user   = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (user.Plan != "pro")
            return Ok(new { active = false, plan = user.Plan });

        // isCancelled = user cancelled but is still within the paid period
        var isCancelled = string.IsNullOrEmpty(user.MpSubscriptionId);

        string? mpStatus = null;
        if (!isCancelled)
        {
            var (s, _) = await FetchPreapproval(user.MpSubscriptionId!);
            mpStatus = s;
        }

        return Ok(new
        {
            active          = true,
            isCancelled,
            plan            = user.Plan,
            mpStatus,
            nextBillingDate = user.NextBillingDate,
            preapprovalId   = user.MpSubscriptionId,
        });
    }

    // ── POST /api/payments/cancel-subscription ────────────────────────────────
    [HttpPost("cancel-subscription")]
    [Authorize]
    public async Task<IActionResult> CancelSubscription()
    {
        var userId = GetUserId();
        var user   = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (user.Plan != "pro")
            return BadRequest(new { message = "No tienes un plan Pro activo." });

        if (string.IsNullOrEmpty(user.MpSubscriptionId))
            return BadRequest(new { message = "La suscripción ya fue cancelada anteriormente." });

        // Cancel in MercadoPago so no future charges happen
        try
        {
            var client = CreateMpClient();
            var resp = await client.PutAsJsonAsync(
                $"https://api.mercadopago.com/preapproval/{Uri.EscapeDataString(user.MpSubscriptionId)}",
                new { status = "cancelled" });

            _logger.LogInformation(
                "MP cancel preapproval: id={Id} httpStatus={Status}",
                user.MpSubscriptionId, resp.StatusCode);

            // 404 = MP ya no la tiene (cancelada antes); "test-" = activada a mano por admin
            if (!resp.IsSuccessStatusCode
                && resp.StatusCode != System.Net.HttpStatusCode.NotFound
                && !user.MpSubscriptionId.StartsWith("test-"))
            {
                var errorBody = await resp.Content.ReadAsStringAsync();
                _logger.LogError("MP cancel preapproval failed: {Status} {Body}", resp.StatusCode, errorBody);
                return StatusCode(502, new { message = "MercadoPago no confirmó la cancelación. Intenta nuevamente." });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling MP preapproval {Id}", user.MpSubscriptionId);
            return StatusCode(502, new { message = "No se pudo contactar a MercadoPago. Intenta nuevamente." });
        }

        // Mark as cancelled locally: clear the subscription ID so MP won't charge again,
        // but KEEP Plan = "pro" and NextBillingDate so the user keeps access
        // until the end of the period they already paid for.
        // SubscriptionExpiryJob will downgrade them when NextBillingDate passes.
        var cancelledMpId = user.MpSubscriptionId;
        user.MpSubscriptionId = null;
        user.NextBillingDate ??= DateTime.UtcNow;

        _context.Payments.Add(new Payment
        {
            UserId      = userId,
            Type        = "pro_cancelled",
            Description = $"Cancelación suscripción Pro. Acceso hasta {user.NextBillingDate?.ToString("dd/MM/yyyy") ?? "fin del período"}",
            AmountCLP   = 0,
            Credits     = 0,
            MpId        = cancelledMpId,
            CreatedAt   = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var accessUntil = user.NextBillingDate?.ToString("dd/MM/yyyy") ?? "tu próximo ciclo";

        _logger.LogInformation(
            "Usuario {UserId} canceló suscripción Pro. Sigue activo hasta {Date}.",
            userId, accessUntil);

        return Ok(new
        {
            message     = $"Suscripción cancelada. Seguirás con Plan Pro hasta el {accessUntil}, luego volverás a Base con {user.CreditsBeforePro ?? 50} créditos.",
            activeUntil = user.NextBillingDate,
            creditsToRestore = user.CreditsBeforePro ?? 50,
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private helpers
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<IActionResult> CreatePreapproval(User user,
        (string Name, int AmountCLP, int Credits, bool IsPro) plan)
    {
        var frontendUrl = FrontendUrl();
        var backendUrl  = BackendUrl();
        var sandbox     = IsSandbox();

        // En sandbox MP exige que el payer sea un usuario de prueba;
        // en producción siempre se usa el email real del usuario.
        var payerEmail = sandbox
            ? EnvConfig.Get("MP_TEST_PAYER_EMAIL") ?? user.Email
            : user.Email;

        var preapprovalObj = new Dictionary<string, object>
        {
            ["reason"]             = plan.Name,
            ["external_reference"] = $"{user.Id}|pro",
            ["payer_email"]        = payerEmail,
            ["back_url"]           = $"{frontendUrl}/billing?success=true&plan=pro",
            ["notification_url"]   = $"{backendUrl}/api/payments/webhook",
            ["auto_recurring"] = new Dictionary<string, object>
            {
                ["frequency"]          = 1,
                ["frequency_type"]     = "months",
                ["transaction_amount"] = (decimal)plan.AmountCLP,
                ["currency_id"]        = "CLP",
            },
        };

        _logger.LogInformation(
            "Creating MP preapproval: user={UserId} amount={Amount} sandbox={Sandbox}",
            user.Id, plan.AmountCLP, sandbox);

        var response = await CreateMpClient().PostAsJsonAsync(
            "https://api.mercadopago.com/preapproval",
            preapprovalObj);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("MP create preapproval failed: {Status} {Body}", response.StatusCode, errorBody);
            return StatusCode(500, new { message = "Error al crear la suscripción." });
        }

        var result        = await response.Content.ReadFromJsonAsync<JsonElement>();
        var preapprovalId = result.GetProperty("id").GetString();
        var initPoint     = result.GetProperty("init_point").GetString();

        _logger.LogInformation(
            "MP preapproval created: user={UserId} id={PreapprovalId}",
            user.Id, preapprovalId);

        return Ok(new { url = initPoint, preapprovalId });
    }

    private async Task<IActionResult> CreatePreference(User user,
        (string Name, int AmountCLP, int Credits, bool IsPro) plan,
        string planId)
    {
        var frontendUrl = FrontendUrl();
        var backendUrl  = BackendUrl();
        var sandbox     = IsSandbox();

        var preferenceObj = new Dictionary<string, object>
        {
            ["items"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["id"]          = planId,
                    ["title"]       = plan.Name,
                    ["description"] = $"+{plan.Credits} créditos para consultas de mercado",
                    ["quantity"]    = 1,
                    ["unit_price"]  = plan.AmountCLP,
                    ["currency_id"] = "CLP",
                }
            },
            ["payer"] = new Dictionary<string, object>
            {
                ["email"] = user.Email ?? "",
                ["name"]  = user.Name ?? "",
            },
            ["back_urls"] = new Dictionary<string, string>
            {
                ["success"] = $"{frontendUrl}/billing?success=true&plan={planId}",
                ["failure"] = $"{frontendUrl}/billing?canceled=true",
                ["pending"] = $"{frontendUrl}/billing?pending=true",
            },
            ["external_reference"] = $"{user.Id}|{planId}",
            ["notification_url"]   = $"{backendUrl}/api/payments/webhook",
            ["statement_descriptor"] = "INMOBISCRAP",
        };

        var isPublicUrl = !frontendUrl.Contains("localhost") && !frontendUrl.Contains("127.0.0.1");
        if (isPublicUrl)
            preferenceObj["auto_return"] = "approved";

        var response = await CreateMpClient().PostAsJsonAsync(
            "https://api.mercadopago.com/checkout/preferences",
            preferenceObj);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("MP create preference failed: {Status} {Body}", response.StatusCode, errorBody);
            return StatusCode(500, new { message = "Error al crear la preferencia de pago." });
        }

        var result       = await response.Content.ReadFromJsonAsync<JsonElement>();
        var preferenceId = result.GetProperty("id").GetString();
        var checkoutUrl  = sandbox
            ? result.GetProperty("sandbox_init_point").GetString()
            : result.GetProperty("init_point").GetString();

        return Ok(new { url = checkoutUrl, preferenceId });
    }

    private async Task HandlePreapprovalWebhook(string preapprovalId)
    {
        var (status, preapproval) = await FetchPreapproval(preapprovalId);

        if (preapproval == null)
        {
            _logger.LogWarning("HandlePreapprovalWebhook: could not fetch preapproval {Id}", preapprovalId);
            return;
        }

        var userId = ExtractUserIdFromReference(preapproval.Value);
        if (userId == null) return;

        _logger.LogInformation(
            "Preapproval webhook: userId={UserId} preapprovalId={Id} status={Status}",
            userId, preapprovalId, status);

        if (status == "authorized")
        {
            await ActivateProSubscription(userId.Value, preapprovalId, preapproval.Value);
        }
        else if (status is "cancelled" or "paused")
        {
            await HandleSubscriptionEnded(userId.Value, preapprovalId, status);
        }
    }

    /// <summary>
    /// La suscripción fue cancelada/pausada desde MercadoPago. Solo afecta si es
    /// la suscripción ACTUAL del usuario (si el usuario ya canceló desde la app,
    /// MpSubscriptionId es null y este webhook no debe quitarle el período pagado).
    /// El usuario conserva Pro hasta NextBillingDate; SubscriptionExpiryJob lo degrada.
    /// </summary>
    private async Task HandleSubscriptionEnded(int userId, string preapprovalId, string status)
    {
        await using var tx = await _context.Database.BeginTransactionAsync();
        var user = await LockUserAsync(userId);
        if (user == null || user.Plan != "pro" || user.MpSubscriptionId != preapprovalId)
            return;

        user.MpSubscriptionId = null;

        if (user.NextBillingDate == null || user.NextBillingDate <= DateTime.UtcNow)
        {
            var restored = user.CreditsBeforePro ?? 50;
            user.Plan             = "base";
            user.Credits          = restored;
            user.CreditsBeforePro = null;
            user.NextBillingDate  = null;
            _logger.LogInformation(
                "Preapproval {Status}: userId={UserId} degradado a base ({Credits} créditos).",
                status, userId, restored);
        }
        else
        {
            _logger.LogInformation(
                "Preapproval {Status}: userId={UserId} conserva Pro hasta {Date}.",
                status, userId, user.NextBillingDate);
        }

        await _context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private async Task ActivateProSubscription(int userId, string preapprovalId, JsonElement preapproval)
    {
        await using var tx = await _context.Database.BeginTransactionAsync();
        var user = await LockUserAsync(userId);
        if (user == null) return;

        var mpNextBilling = ExtractNextBillingDate(preapproval);

        // Misma suscripción ya activa → solo es renovación si MP avanzó la fecha de cobro.
        // (MP manda webhooks por cualquier cambio del preapproval; sin esta comparación
        //  cada webhook/verify registraba un "pro_renewed" duplicado.)
        if (user.Plan == "pro" && user.MpSubscriptionId == preapprovalId)
        {
            if (mpNextBilling.HasValue
                && (user.NextBillingDate == null || mpNextBilling.Value > user.NextBillingDate.Value.AddDays(1)))
            {
                user.NextBillingDate = mpNextBilling;
                _context.Payments.Add(new Payment
                {
                    UserId      = user.Id,
                    Type        = "pro_renewed",
                    Description = "Renovación mensual Plan Pro",
                    AmountCLP   = ProAmountCLP,
                    Credits     = 0,
                    MpId        = preapprovalId,
                    CreatedAt   = DateTime.UtcNow,
                });
                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                _logger.LogInformation(
                    "Preapproval renewal: userId={UserId} nextBilling={Date}",
                    user.Id, user.NextBillingDate);
            }
            return;
        }

        // First activation
        if (user.Plan != "pro")
            user.CreditsBeforePro = user.Credits;

        user.Plan             = "pro";
        user.Credits          = 0;
        user.MpSubscriptionId = preapprovalId;
        user.NextBillingDate  = mpNextBilling ?? DateTime.UtcNow.AddMonths(1);

        _context.Payments.Add(new Payment
        {
            UserId      = user.Id,
            Type        = "pro_activated",
            Description = "Activación Plan Pro mensual",
            AmountCLP   = ProAmountCLP,
            Credits     = 0,
            MpId        = preapprovalId,
            CreatedAt   = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        _logger.LogInformation(
            "Pro activated: userId={UserId} preapprovalId={Id} nextBilling={Date}",
            user.Id, preapprovalId, user.NextBillingDate);
    }

    private async Task<(string status, JsonElement? preapproval)> FetchPreapproval(string preapprovalId)
    {
        try
        {
            var response = await CreateMpClient().GetAsync(
                $"https://api.mercadopago.com/preapproval/{Uri.EscapeDataString(preapprovalId)}");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("FetchPreapproval failed: id={Id} status={Status}", preapprovalId, response.StatusCode);
                return ("error", null);
            }

            var json   = await response.Content.ReadFromJsonAsync<JsonElement>();
            var status = json.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
            return (status, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FetchPreapproval exception: id={Id}", preapprovalId);
            return ("error", null);
        }
    }

    /// <summary>external_reference tiene formato "{userId}|{planId}".</summary>
    private static int? ExtractUserIdFromReference(JsonElement mpObject)
    {
        if (!mpObject.TryGetProperty("external_reference", out var er) || er.ValueKind != JsonValueKind.String)
            return null;
        var parts = (er.GetString() ?? "").Split('|');
        return parts.Length == 2 && int.TryParse(parts[0], out var id) ? id : null;
    }

    private static DateTime? ExtractNextBillingDate(JsonElement preapproval)
    {
        // MP returns next_payment_date at root level (and sometimes inside auto_recurring)
        foreach (var candidate in new[] { preapproval, preapproval.TryGetProperty("auto_recurring", out var ar) ? ar : default })
        {
            if (candidate.ValueKind == JsonValueKind.Object
                && candidate.TryGetProperty("next_payment_date", out var npd)
                && npd.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(npd.GetString(), out var dt))
                return dt.UtcDateTime;
        }
        return null;
    }

    private static string? ExtractIdFromResource(string resource)
    {
        if (string.IsNullOrEmpty(resource)) return null;
        if (resource.StartsWith("http"))
        {
            var last = resource.Split('/').LastOrDefault();
            return string.IsNullOrEmpty(last) ? null : last;
        }
        return resource;
    }

    // ── One-time payment verification (for credit packs) ──────────────────────
    private async Task<string> VerifyAndApplyPaymentAsync(string paymentId, int? expectedUserId)
    {
        if (string.IsNullOrWhiteSpace(paymentId) || !paymentId.All(char.IsDigit))
            return "error";

        var response = await CreateMpClient().GetAsync($"https://api.mercadopago.com/v1/payments/{paymentId}");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MP payment fetch failed: id={Id} status={Status}", paymentId, response.StatusCode);
            return "error";
        }

        var payment = await response.Content.ReadFromJsonAsync<JsonElement>();

        var status = payment.TryGetProperty("status", out var st) ? st.GetString() : null;
        var externalReference = payment.TryGetProperty("external_reference", out var er)
                                && er.ValueKind == JsonValueKind.String ? er.GetString() : null;

        _logger.LogInformation("MP payment status: id={Id} status={Status} ref={Ref}",
            paymentId, status, externalReference);

        var parts = (externalReference ?? "").Split('|');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var userId)
            || !Plans.TryGetValue(parts[1], out var plan))
        {
            _logger.LogWarning("MP: external_reference inválido: '{Ref}'", externalReference);
            return "error";
        }

        if (expectedUserId.HasValue && expectedUserId.Value != userId)
        {
            _logger.LogWarning("MP: userId mismatch. Expected={E} Got={G}", expectedUserId, userId);
            return "forbidden";
        }

        if (status != "approved")
            return status ?? "unknown";

        // Skip subscription payments handled via preapproval webhook
        if (plan.IsPro) return "handled_by_subscription";

        // Validar que el monto cobrado corresponda al pack
        if (payment.TryGetProperty("transaction_amount", out var amountProp)
            && amountProp.ValueKind == JsonValueKind.Number
            && amountProp.GetDecimal() < plan.AmountCLP)
        {
            _logger.LogWarning("MP: monto {Amount} menor al precio del pack {Plan} ({Price})",
                amountProp.GetDecimal(), parts[1], plan.AmountCLP);
            return "error";
        }

        // ── Idempotencia: lock de la fila del usuario + chequeo de pago ya aplicado.
        // Webhook, reintentos de MP y /verify pueden llegar en paralelo con el mismo id.
        await using var tx = await _context.Database.BeginTransactionAsync();

        var user = await LockUserAsync(userId);
        if (user == null)
        {
            _logger.LogWarning("MP: usuario {UserId} no encontrado.", userId);
            return "error";
        }

        var alreadyApplied = await _context.Payments
            .AnyAsync(p => p.MpId == paymentId && p.Type == parts[1]);
        if (alreadyApplied)
        {
            _logger.LogInformation("MP: pago {PaymentId} ya aplicado previamente.", paymentId);
            return "already_applied";
        }

        user.Credits += plan.Credits;
        _logger.LogInformation("MP: usuario {UserId} +{Credits} créditos → {Total}.",
            userId, plan.Credits, user.Credits);

        _context.Payments.Add(new Payment
        {
            UserId      = user.Id,
            Type        = parts[1],
            Description = plan.Name,
            AmountCLP   = plan.AmountCLP,
            Credits     = plan.Credits,
            MpId        = paymentId,
            CreatedAt   = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        await tx.CommitAsync();
        return "approved";
    }
}
