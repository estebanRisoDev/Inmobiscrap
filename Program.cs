using System.Text;
using Inmobiscrap.Data;
using Inmobiscrap.Services;
using Inmobiscrap.Jobs;
using Inmobiscrap.Hubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
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
        policy.WithOrigins(
                "http://localhost:3000",
                "http://localhost:3001",
                "http://127.0.0.1:3000",
                "http://127.0.0.1:3001")
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

builder.Services.AddAuthorization();

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

// ── SERVICES ──────────────────────────────────────────────────────
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IBotLogBuffer, BotLogBuffer>();
builder.Services.AddScoped<IScraperService, ScraperService>();
builder.Services.AddScoped<IBotLogService, BotLogService>();
builder.Services.AddScoped<IPropertyUpsertService, PropertyUpsertService>();  // ← AGREGAR
builder.Services.AddScoped<ScrapingJob>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        In          = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Ingresa: Bearer {token}",
        Name        = "Authorization",
        Type        = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
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
// Resetea bots zombie que quedaron en running/stopping por un Docker restart
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
// Si SUPERADMIN_EMAIL está configurado, promueve automáticamente al usuario
// existente (si ya se registró) o lo marca para auto-promoción al login.
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
// Sincroniza los jobs de Hangfire con el estado real de la BD.
// Necesario porque Hangfire guarda los jobs en su propia tabla de PostgreSQL,
// pero si un bot fue modificado mientras el servidor estaba caído, pueden
// quedar desincronizados. Este bloque deja Hangfire como fuente de verdad
// secundaria, siendo la BD la fuente primaria.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db              = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var recurringJobs   = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

        var allBots = await db.Bots.ToListAsync();

        int registered = 0;
        int removed    = 0;

        foreach (var bot in allBots)
        {
            var jobId = $"bot-schedule-{bot.Id}";

            if (bot.IsActive && bot.ScheduleEnabled && !string.IsNullOrWhiteSpace(bot.CronExpression))
            {
                // Registrar o actualizar job de Hangfire para este bot
                recurringJobs.AddOrUpdate<ScrapingJob>(
                    jobId,
                    job => job.ExecuteBotAsync(bot.Id),
                    bot.CronExpression);
                registered++;
            }
            else
            {
                // Eliminar job si el bot ya no debe estar programado
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
    app.UseSwaggerUI();
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
// Job global para bots sin schedule propio (idle/error sin cron configurado)
using (var scope = app.Services.CreateScope())
{
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<ScrapingJob>(
        "execute-all-bots",
        job => job.ExecuteAllActiveBotsAsync(),
        Cron.Hourly());
}

var port = Environment.GetEnvironmentVariable("API_PORT") ?? "5000";
Console.WriteLine($"🚀 API corriendo en http://localhost:{port}");
Console.WriteLine($"📊 Swagger UI: http://localhost:{port}/swagger");
Console.WriteLine($"🔐 Auth endpoints: /api/auth/register | /api/auth/login | /api/auth/google");

app.Run();