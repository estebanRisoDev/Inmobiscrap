using System.Security.Claims;
using System.Text;
using Inmobiscrap.Data;
using Inmobiscrap.Services;
using Inmobiscrap.Jobs;
using Inmobiscrap.Hubs;
using Inmobiscrap.Filters;
using Inmobiscrap.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Hangfire;
using Hangfire.PostgreSql;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using DotNetEnv;

// NoClobber: las variables ya definidas (docker-compose `environment:`) ganan
// sobre el .env, en vez de que el .env montado en /app las pise.
Env.NoClobber().Load();

var builder = WebApplication.CreateBuilder(args);

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocalhost", policy =>
    {
        var origins = new List<string>
        {
            "http://localhost:3000",
            "http://localhost:3001",
            "http://127.0.0.1:3000",
            "http://127.0.0.1:3001",
            "https://app.prismainmobiliario.cl",
        };

        // Include FRONTEND_URL so MP redirect origin is allowed
        var frontendUrl = EnvConfig.Get("FRONTEND_URL");
        if (frontendUrl != null)
            origins.Add(frontendUrl.TrimEnd('/'));

        policy.WithOrigins(origins.ToArray())
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ── DATABASE ─────────────────────────────────────────────────────────────────
var connectionString = EnvConfig.Get("DB_CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "Falta la conexión a la BD: define DB_CONNECTION_STRING o ConnectionStrings__DefaultConnection.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── JWT AUTHENTICATION ────────────────────────────────────────────────────────
var jwtSecret = EnvConfig.Get("JWT_SECRET")
    ?? builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("JWT_SECRET must be set in environment variables.");

// HS256 exige una clave de al menos 256 bits; si no, falla recién al firmar el primer token.
if (Encoding.UTF8.GetByteCount(jwtSecret) < 32)
    throw new InvalidOperationException("JWT_SECRET debe tener al menos 32 caracteres.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ValidateIssuer           = true,
        ValidIssuer              = builder.Configuration["Jwt:Issuer"] ?? "inmobiscrap",
        ValidateAudience         = true,
        ValidAudience            = builder.Configuration["Jwt:Audience"] ?? "inmobiscrap",
        ValidateLifetime         = true,
        ClockSkew                = TimeSpan.Zero,
    };

    // Soporte para SignalR: leer token desde query string
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                context.Token = accessToken;
            return Task.CompletedTask;
        },

        // El JWT dura 7 días: re-leer rol/plan/estado desde la BD en cada request
        // para que downgrades, cambios de rol y desactivaciones apliquen de inmediato.
        OnTokenValidated = async context =>
        {
            var identity = context.Principal?.Identity as ClaimsIdentity;
            var idClaim  = identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (identity == null || !int.TryParse(idClaim, out var userId))
            {
                context.Fail("Token sin usuario válido.");
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
            var current = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.IsActive, u.Role, u.Plan })
                .FirstOrDefaultAsync();

            if (current == null || !current.IsActive)
            {
                context.Fail("Usuario inexistente o desactivado.");
                return;
            }

            foreach (var claim in identity.FindAll(identity.RoleClaimType).ToList())
                identity.RemoveClaim(claim);
            foreach (var claim in identity.FindAll("plan").ToList())
                identity.RemoveClaim(claim);

            identity.AddClaim(new Claim(identity.RoleClaimType, current.Role));
            identity.AddClaim(new Claim("plan", current.Plan));
        },
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ProOrAdmin", policy =>
        policy.RequireAssertion(context =>
            context.User.IsInRole("admin") ||
            context.User.HasClaim("plan", "pro")));
});

// ── AWS BEDROCK ───────────────────────────────────────────────────────────────
var awsAccessKey = EnvConfig.Get("AWS_ACCESS_KEY_ID");
var awsSecretKey = EnvConfig.Get("AWS_SECRET_ACCESS_KEY");
// El modelo por defecto es un perfil "us." → requiere una región de EE.UU.
var awsRegion    = EnvConfig.Get("AWS_REGION") ?? "us-east-1";
var mockScraping = EnvConfig.GetBool("SCRAPER_MOCK_MODE");

AWSCredentials credentials;
if (awsAccessKey != null && awsSecretKey != null)
{
    credentials = new BasicAWSCredentials(awsAccessKey, awsSecretKey);
}
else if (mockScraping)
{
    // En modo mock Bedrock nunca se llama: se permite arrancar sin credenciales.
    Console.WriteLine("⚠️  SCRAPER_MOCK_MODE=true y sin credenciales AWS: Bedrock deshabilitado.");
    credentials = new AnonymousAWSCredentials();
}
else
{
    throw new InvalidOperationException(
        "AWS credentials not found (AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY). Usa SCRAPER_MOCK_MODE=true para probar sin AWS.");
}

builder.Services.AddSingleton<IAmazonBedrockRuntime>(
    new AmazonBedrockRuntimeClient(credentials, Amazon.RegionEndpoint.GetBySystemName(awsRegion)));

// ── HANGFIRE ──────────────────────────────────────────────────────────────────
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString)));

builder.Services.AddHangfireServer();

// ── SERVICES ──────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IBotLogBuffer, BotLogBuffer>();
builder.Services.AddScoped<IScraperService, ScraperService>();
builder.Services.AddScoped<IBotLogService, BotLogService>();
builder.Services.AddScoped<IPropertyUpsertService, PropertyUpsertService>();
builder.Services.AddScoped<IReportService, ReportService>(); 
builder.Services.AddScoped<ScrapingJob>();
builder.Services.AddScoped<SubscriptionExpiryJob>();
builder.Services.AddScoped<IPropertyVerificationService, PropertyVerificationService>();
builder.Services.AddScoped<PropertyVerificationJob>();
builder.Services.AddSingleton<IVerificationJobStatus, VerificationJobStatus>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<PriceAlertJob>();
builder.Services.AddScoped<ICreditService, CreditService>();

builder.Services.AddScoped<DevelopmentOnlyFilter>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();


// ── SWAGGER con JWT Bearer ────────────────────────────────────────────────────
// FIX: usar SecuritySchemeType.Http + Scheme "bearer" en vez de ApiKey.
// Con esto el botón "Authorize 🔒" solo pide el token, sin prefijo "Bearer ".
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title   = "Inmobiscrap API",
        Version = "v1",
        Description = "API de scraping inmobiliario. Para autenticarte: llama a /api/auth/login, copia el token y pégalo en el botón Authorize 🔒"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = SecuritySchemeType.Http,   // ← Http, no ApiKey
        Scheme       = "bearer",                  // ← lowercase "bearer"
        BearerFormat = "JWT",
        In           = ParameterLocation.Header,
        Description  = "Pega tu JWT token aquí (sin el prefijo 'Bearer ').",
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id   = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ── AUTO-MIGRATE ──────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (db.Database.GetPendingMigrations().Any())
        {
            Console.WriteLine("Applying pending migrations...");
            db.Database.Migrate();
            Console.WriteLine("✅ Migrations applied.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Migration error: {ex.Message}");
    }
}

// ── STARTUP BOT CLEANUP ───────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var stuckBots = await db.Bots
            .Where(b => b.Status == "running" || b.Status == "stopping")
            .ToListAsync();

        if (stuckBots.Any())
        {
            foreach (var bot in stuckBots)
            {
                bot.Status    = "idle";
                bot.UpdatedAt = DateTime.UtcNow;
                bot.LastError = "Estado reseteado: proceso interrumpido por reinicio del servidor.";
            }
            await db.SaveChangesAsync();
            Console.WriteLine($"⚠️  Reset {stuckBots.Count} bot(s) zombie → idle.");
        }
        else
        {
            Console.WriteLine("✅ No hay bots zombie al arrancar.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Startup bot cleanup error: {ex.Message}");
    }
}

// ── SUPERADMIN SEED ───────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var superAdminEmails = EnvConfig.Get("SUPERADMIN_EMAIL") ?? "";

        var emails = superAdminEmails
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLower())
            .ToList();

        if (emails.Any())
        {
            // Solo cuentas cuyo email fue verificado por Google (GoogleId != null).
            // Una cuenta con contraseña pudo registrarla cualquiera con ese email.
            var usersToPromote = await db.Users
                .Where(u => emails.Contains(u.Email) && u.Role != "admin" && u.GoogleId != null)
                .ToListAsync();

            foreach (var user in usersToPromote)
            {
                user.Role = "admin";
                user.Plan = "pro";
                Console.WriteLine($"🔑 Promoted existing user to admin: {user.Email}");
            }

            if (usersToPromote.Any())
                await db.SaveChangesAsync();

            Console.WriteLine($"✅ SuperAdmin emails configured: {string.Join(", ", emails)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"SuperAdmin seed error: {ex.Message}");
    }
}

// ── STARTUP HANGFIRE SYNC ─────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db            = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

        var allBots = await db.Bots.ToListAsync();

        int registered = 0;
        int removed    = 0;

        int failed     = 0;

        foreach (var bot in allBots)
        {
            var jobId = $"bot-schedule-{bot.Id}";

            // try por bot: un cron inválido no debe impedir registrar los demás
            try
            {
                if (bot.IsActive && bot.ScheduleEnabled && CronValidator.IsValid(bot.CronExpression, out _))
                {
                    var botId = bot.Id;
                    recurringJobs.AddOrUpdate<ScrapingJob>(
                        jobId,
                        job => job.ExecuteBotAsync(botId),
                        bot.CronExpression!.Trim());
                    registered++;
                }
                else
                {
                    if (bot.IsActive && bot.ScheduleEnabled)
                        Console.WriteLine($"⚠️  Bot {bot.Id}: CronExpression inválida '{bot.CronExpression}', no se programa.");
                    recurringJobs.RemoveIfExists(jobId);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"Hangfire sync error en bot {bot.Id}: {ex.Message}");
            }
        }

        Console.WriteLine($"✅ Hangfire sync: {registered} job(s) registrado(s), {removed} eliminado(s), {failed} con error.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Hangfire sync error: {ex.Message}");
    }
}

// ── PIPELINE ──────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Inmobiscrap API v1");
        c.DisplayRequestDuration();
    });
    app.UseHangfireDashboard("/hangfire");
}

app.UseCors("AllowLocalhost");
app.UseHttpsRedirection();

// ORDEN IMPORTANTE: Authentication ANTES de Authorization
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<BotLogHub>("/hubs/botlogs");

// ── RECURRING JOB GLOBAL ──────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<ScrapingJob>(
        "execute-all-bots",
        job => job.ExecuteAllActiveBotsAsync(),
        Cron.Hourly());

    recurringJobs.AddOrUpdate<SubscriptionExpiryJob>(
        "check-subscription-expiry",
        job => job.CheckAndExpireSubscriptionsAsync(),
        Cron.Daily());

    recurringJobs.AddOrUpdate<PropertyVerificationJob>(
        "verify-sold-properties-am",
        job => job.ExecuteAsync(),
        Cron.Daily(3, 0)); // 3:00 AM UTC

    recurringJobs.AddOrUpdate<PropertyVerificationJob>(
        "verify-sold-properties-pm",
        job => job.ExecuteAsync(),
        Cron.Daily(15, 0)); // 3:00 PM UTC

    recurringJobs.AddOrUpdate<PriceAlertJob>(
        "send-price-alerts",
        job => job.ExecuteAsync(),
        Cron.Daily(8, 0)); // Enviar alertas de precio diariamente a las 8:00 AM
}

var port = EnvConfig.Get("API_PORT") ?? "5000";
Console.WriteLine($"🚀 API corriendo en http://localhost:{port}");
Console.WriteLine($"📊 Swagger UI: http://localhost:{port}/swagger");
Console.WriteLine($"🔐 Auth endpoints: /api/auth/register | /api/auth/login | /api/auth/google");

app.Run();