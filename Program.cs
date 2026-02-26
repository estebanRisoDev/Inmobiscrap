using Inmobiscrap.Data;
using Inmobiscrap.Services;
using Inmobiscrap.Jobs;
using Inmobiscrap.Hubs; // ← AGREGAR ESTO
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Hangfire.PostgreSql;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using DotNetEnv;

// Cargar variables de entorno desde .env
Env.Load();

var builder = WebApplication.CreateBuilder(args);

// ⭐ CONFIGURAR CORS - IMPORTANTE PARA EL FRONTEND
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocalhost",
        builder =>
        {
            builder.WithOrigins(
                    "http://localhost:3000",  // Next.js dev server
                    "http://localhost:3001",  // Por si usas otro puerto
                    "http://127.0.0.1:3000",
                    "http://127.0.0.1:3001"
                )
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    
    // Política más permisiva para desarrollo (usa con cuidado)
    options.AddPolicy("AllowAll",
        builder =>
        {
            builder.AllowAnyOrigin()
                   .AllowAnyHeader()
                   .AllowAnyMethod();
        });
});

// Configurar DbContext con PostgreSQL
var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") 
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configurar AWS Bedrock Client usando variables de entorno
var awsAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
var awsSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
var awsRegion = Environment.GetEnvironmentVariable("AWS_REGION") ?? "sa-east-1";

if (string.IsNullOrEmpty(awsAccessKey) || string.IsNullOrEmpty(awsSecretKey))
{
    throw new InvalidOperationException(
        "AWS credentials not found. Please set AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY environment variables.");
}

var credentials = new BasicAWSCredentials(awsAccessKey, awsSecretKey);
builder.Services.AddSingleton<IAmazonBedrockRuntime>(
    new AmazonBedrockRuntimeClient(credentials, Amazon.RegionEndpoint.GetBySystemName(awsRegion)));

// Configurar Hangfire
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => 
        options.UseNpgsqlConnection(connectionString)));

builder.Services.AddHangfireServer();

// Agregar HttpClient
builder.Services.AddHttpClient();

// ⭐ AGREGAR SIGNALR (ESTO FALTABA)
builder.Services.AddSignalR();

// Registrar servicios
builder.Services.AddSingleton<IBotLogBuffer, BotLogBuffer>(); // Singleton: persiste historial en memoria
builder.Services.AddScoped<IScraperService, ScraperService>();
builder.Services.AddScoped<IBotLogService, BotLogService>(); // ← ESTO FALTABA
builder.Services.AddScoped<ScrapingJob>();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ⭐ APLICAR MIGRACIONES AUTOMÁTICAMENTE (Opcional pero útil)
using (var scope = app.Services.CreateScope())
{
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        // Verificar si hay migraciones pendientes
        if (dbContext.Database.GetPendingMigrations().Any())
        {
            Console.WriteLine("Aplicando migraciones pendientes...");
            dbContext.Database.Migrate();
            Console.WriteLine("Migraciones aplicadas exitosamente.");
        }
        else
        {
            Console.WriteLine("No hay migraciones pendientes.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error al aplicar migraciones: {ex.Message}");
        // En desarrollo podrías querer que falle, en producción tal vez solo log
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    
    // Dashboard de Hangfire (solo en desarrollo)
    app.UseHangfireDashboard("/hangfire");
}

// ⭐ USAR CORS - MUY IMPORTANTE QUE VAYA ANTES DE Authorization
app.UseCors("AllowLocalhost"); // Usa "AllowAll" para desarrollo si tienes problemas

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// ⭐ MAPEAR HUB DE SIGNALR (ESTO FALTABA)
app.MapHub<BotLogHub>("/hubs/botlogs");

// Programar jobs DESPUÉS de que app esté construida
using (var scope = app.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    
    recurringJobManager.AddOrUpdate<ScrapingJob>(
        "execute-all-bots",
        job => job.ExecuteAllActiveBotsAsync(),
        Cron.Hourly());
}

// ⭐ Log para confirmar en qué puerto está corriendo
var port = Environment.GetEnvironmentVariable("API_PORT") ?? "5000";
Console.WriteLine($"🚀 API corriendo en http://localhost:{port}");
Console.WriteLine($"📊 Swagger UI disponible en http://localhost:{port}/swagger");
Console.WriteLine($"🔧 Hangfire Dashboard en http://localhost:{port}/hangfire");
Console.WriteLine($"📡 SignalR Hub disponible en ws://localhost:{port}/hubs/botlogs");

app.Run();