using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Models;

namespace Inmobiscrap.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Property> Properties { get; set; } = null!;
    public DbSet<Bot> Bots { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ============ CONFIGURACIÓN DE PROPERTY ============
        modelBuilder.Entity<Property>(entity =>
        {
            // Propiedades requeridas
            entity.Property(p => p.Title)
                .IsRequired()
                .HasMaxLength(500);
            
            // Strings opcionales
            entity.Property(p => p.Currency)
                .HasMaxLength(10)
                .HasDefaultValue("CLP");
            
            entity.Property(p => p.Address).HasMaxLength(500);
            entity.Property(p => p.City).HasMaxLength(100);
            entity.Property(p => p.Region).HasMaxLength(100);
            entity.Property(p => p.Neighborhood).HasMaxLength(200);
            entity.Property(p => p.PropertyType).HasMaxLength(50);
            
            entity.Property(p => p.Description)
                .HasColumnType("TEXT"); // PostgreSQL
            
            // Decimales
            entity.Property(p => p.Price).HasPrecision(18, 2);
            entity.Property(p => p.Area).HasPrecision(10, 2);
            
            // Índices para búsquedas de usuarios
            entity.HasIndex(p => new { p.City, p.PropertyType })
                .HasDatabaseName("IX_Properties_City_Type");
            
            entity.HasIndex(p => p.City)
                .HasDatabaseName("IX_Properties_City");
            
            entity.HasIndex(p => p.Price)
                .HasDatabaseName("IX_Properties_Price");
            
        });

        // ============ CONFIGURACIÓN DE BOT ============
        modelBuilder.Entity<Bot>(entity =>
        {
            // Propiedades requeridas
            entity.Property(b => b.Name)
                .IsRequired()
                .HasMaxLength(200);
            
            entity.Property(b => b.Source)
                .IsRequired()
                .HasMaxLength(50);
            
            entity.Property(b => b.Url)
                .IsRequired()
                .HasMaxLength(2000);
            
            entity.Property(b => b.Status)
                .HasMaxLength(50)
                .HasDefaultValue("idle");
            
            entity.Property(b => b.LastError)
                .HasMaxLength(1000);
            
            // Valores por defecto
            entity.Property(b => b.IsActive)
                .HasDefaultValue(true);
            
            entity.Property(b => b.TotalScraped)
                .HasDefaultValue(0);
            
            entity.Property(b => b.LastRunCount)
                .HasDefaultValue(0);
            
            entity.Property(b => b.CreatedAt)
                .HasDefaultValueSql("NOW()"); // PostgreSQL
            
            // Índices
            entity.HasIndex(b => b.Source)
                .HasDatabaseName("IX_Bots_Source");
            
            entity.HasIndex(b => b.IsActive)
                .HasDatabaseName("IX_Bots_IsActive");
            
            entity.HasIndex(b => b.Status)
                .HasDatabaseName("IX_Bots_Status");
        });
    }
}