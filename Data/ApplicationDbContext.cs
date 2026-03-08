using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Models;

namespace Inmobiscrap.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<Property>         Properties { get; set; } = null!;
    public DbSet<PropertySnapshot> PropertySnapshots { get; set; } = null!;
    public DbSet<Bot>              Bots       { get; set; } = null!;
    public DbSet<User>             Users      { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ============ PROPERTY ============
        modelBuilder.Entity<Property>(entity =>
        {
            entity.Property(p => p.Title).IsRequired().HasMaxLength(500);
            entity.Property(p => p.Currency).HasMaxLength(10).HasDefaultValue("CLP");
            entity.Property(p => p.Address).HasMaxLength(500);
            entity.Property(p => p.City).HasMaxLength(100);
            entity.Property(p => p.Region).HasMaxLength(100);
            entity.Property(p => p.Neighborhood).HasMaxLength(200);
            entity.Property(p => p.SourceUrl).HasMaxLength(2000);
            entity.Property(p => p.PropertyType).HasMaxLength(50);
            entity.Property(p => p.Description).HasColumnType("TEXT");
            entity.Property(p => p.Price).HasPrecision(18, 2);
            entity.Property(p => p.Area).HasPrecision(10, 2);

            // Tracking
            entity.Property(p => p.Fingerprint).HasMaxLength(64);
            entity.Property(p => p.ListingStatus).HasMaxLength(30).HasDefaultValue("active");
            entity.Property(p => p.TimesScraped).HasDefaultValue(1);
            entity.Property(p => p.PreviousPrice).HasPrecision(18, 2);

            entity.HasIndex(p => p.Fingerprint).HasDatabaseName("IX_Properties_Fingerprint");
            entity.HasIndex(p => p.SourceUrl).HasDatabaseName("IX_Properties_SourceUrl");
            entity.HasIndex(p => p.ListingStatus).HasDatabaseName("IX_Properties_ListingStatus");
            entity.HasIndex(p => new { p.City, p.PropertyType }).HasDatabaseName("IX_Properties_City_Type");
            entity.HasIndex(p => p.City).HasDatabaseName("IX_Properties_City");
            entity.HasIndex(p => p.Price).HasDatabaseName("IX_Properties_Price");
        });

        // ============ PROPERTY SNAPSHOT ============
        modelBuilder.Entity<PropertySnapshot>(entity =>
        {
            entity.Property(s => s.ScrapedAt).HasDefaultValueSql("NOW()");
            entity.Property(s => s.Currency).HasMaxLength(10);
            entity.Property(s => s.PropertyType).HasMaxLength(50);
            entity.Property(s => s.Title).HasMaxLength(500);
            entity.Property(s => s.Price).HasPrecision(18, 2);
            entity.Property(s => s.Area).HasPrecision(10, 2);
            entity.Property(s => s.HasChanges).HasDefaultValue(false);
            entity.Property(s => s.ChangedFields).HasMaxLength(200);

            // ── Ubicación desnormalizada (nuevo) ──────────────────────────────
            entity.Property(s => s.Region).HasMaxLength(100);
            entity.Property(s => s.City).HasMaxLength(100);
            entity.Property(s => s.Neighborhood).HasMaxLength(200);

            entity.HasOne(s => s.Property)
                  .WithMany(p => p.Snapshots)
                  .HasForeignKey(s => s.PropertyId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Índices para consultas rápidas
            entity.HasIndex(s => s.PropertyId).HasDatabaseName("IX_Snapshots_PropertyId");
            entity.HasIndex(s => s.ScrapedAt).HasDatabaseName("IX_Snapshots_ScrapedAt");
            entity.HasIndex(s => s.BotId).HasDatabaseName("IX_Snapshots_BotId");
            entity.HasIndex(s => new { s.PropertyId, s.ScrapedAt })
                  .HasDatabaseName("IX_Snapshots_Property_Date");

            // ── Índices nuevos para filtros por ubicación en price-history ────
            entity.HasIndex(s => s.City).HasDatabaseName("IX_Snapshots_City");
            entity.HasIndex(s => s.Region).HasDatabaseName("IX_Snapshots_Region");
            entity.HasIndex(s => new { s.ScrapedAt, s.Currency })
                  .HasDatabaseName("IX_Snapshots_ScrapedAt_Currency");
        });

        // ============ BOT ============
        modelBuilder.Entity<Bot>(entity =>
        {
            entity.Property(b => b.Name).IsRequired().HasMaxLength(200);
            entity.Property(b => b.Source).IsRequired().HasMaxLength(50);
            entity.Property(b => b.Url).IsRequired().HasMaxLength(2000);
            entity.Property(b => b.Status).HasMaxLength(50).HasDefaultValue("idle");
            entity.Property(b => b.LastError).HasMaxLength(1000);
            entity.Property(b => b.IsActive).HasDefaultValue(true);
            entity.Property(b => b.TotalScraped).HasDefaultValue(0);
            entity.Property(b => b.LastRunCount).HasDefaultValue(0);
            entity.Property(b => b.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(b => b.ScheduleEnabled).HasDefaultValue(false);
            entity.Property(b => b.CronExpression).HasMaxLength(100);

            entity.HasOne(b => b.User)
                  .WithMany(u => u.Bots)
                  .HasForeignKey(b => b.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(b => b.UserId).HasDatabaseName("IX_Bots_UserId");
            entity.HasIndex(b => b.Source).HasDatabaseName("IX_Bots_Source");
            entity.HasIndex(b => b.IsActive).HasDatabaseName("IX_Bots_IsActive");
            entity.HasIndex(b => b.Status).HasDatabaseName("IX_Bots_Status");
        });

        // ============ USER ============
        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(u => u.Email).IsRequired().HasMaxLength(320);
            entity.Property(u => u.Name).IsRequired().HasMaxLength(200);
            entity.Property(u => u.PasswordHash).HasMaxLength(100);
            entity.Property(u => u.GoogleId).HasMaxLength(100);
            entity.Property(u => u.AvatarUrl).HasMaxLength(500);
            entity.Property(u => u.Role).HasMaxLength(20).HasDefaultValue("user");
            entity.Property(u => u.IsActive).HasDefaultValue(true);
            entity.Property(u => u.CreatedAt).HasDefaultValueSql("NOW()");
            entity.Property(u => u.Plan).HasMaxLength(20).HasDefaultValue("base");
            entity.Property(u => u.Credits).HasDefaultValue(50);

            entity.HasIndex(u => u.Email).IsUnique().HasDatabaseName("IX_Users_Email");
            entity.HasIndex(u => u.GoogleId).HasDatabaseName("IX_Users_GoogleId");
        });
    }
}