using System.Text;
using Inmobiscrap.Data;
using Inmobiscrap.Services;
using Inmobiscrap.Jobs;
using Inmobiscrap.Hubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Hangfire;
using Hangfire.PostgreSql;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using DotNetEnv;

Env.Load();

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
        };

        // Include FRONTEND_URL so MP redirect origin is allowed
        var frontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL");
        if (!string.IsNullOrEmpty(frontendUrl))
            origins.Add(frontendUrl.TrimEnd('/'));

        policy.WithOrigins(origins.ToArray())
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ── DATABASE ─────────────────────────────────────────────────────────────────
var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── JWT AUTHENTICATION ────────────────────────────────────────────────────────
var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("JWT_SECRET must be set in environment variables.");

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
        }
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
var awsAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
var awsSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
var awsRegion    = Environment.GetEnvironmentVariable("AWS_REGION") ?? "sa-east-1";

if (string.IsNullOrEmpty(awsAccessKey) || string.IsNullOrEmpty(awsSecretKey))
    throw new InvalidOperationException("AWS credentials not found.");

var credentials = new BasicAWSCredentials(awsAccessKey, awsSecretKey);
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
        var superAdminEmails = Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL") ?? "";

        var emails = superAdminEmails
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLower())
            .ToList();

        if (emails.Any())
        {
            var usersToPromote = await db.Users
                .Where(u => emails.Contains(u.Email) && u.Role != "admin")
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

        foreach (var bot in allBots)
        {
            var jobId = $"bot-schedule-{bot.Id}";

            if (bot.IsActive && bot.ScheduleEnabled && !string.IsNullOrWhiteSpace(bot.CronExpression))
            {
                recurringJobs.AddOrUpdate<ScrapingJob>(
                    jobId,
                    job => job.ExecuteBotAsync(bot.Id),
                    bot.CronExpression);
                registered++;
            }
            else
            {
                recurringJobs.RemoveIfExists(jobId);
                removed++;
            }
        }

        Console.WriteLine($"✅ Hangfire sync: {registered} job(s) registrado(s), {removed} eliminado(s).");
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
        "verify-sold-properties",
        job => job.ExecuteAsync(),
        Cron.Daily(3, 0)); // Ejecutar diariamente a las 3:00 AM
}

var port = Environment.GetEnvironmentVariable("API_PORT") ?? "5000";
Console.WriteLine($"🚀 API corriendo en http://localhost:{port}");
Console.WriteLine($"📊 Swagger UI: http://localhost:{port}/swagger");
Console.WriteLine($"🔐 Auth endpoints: /api/auth/register | /api/auth/login | /api/auth/google");

app.Run();