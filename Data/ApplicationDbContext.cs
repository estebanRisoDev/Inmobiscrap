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
    public DbSet<Bot>      Bots       { get; set; } = null!;
    public DbSet<User>     Users      { get; set; } = null!;

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

            entity.HasIndex(p => new { p.City, p.PropertyType }).HasDatabaseName("IX_Properties_City_Type");
            entity.HasIndex(p => p.City).HasDatabaseName("IX_Properties_City");
            entity.HasIndex(p => p.Price).HasDatabaseName("IX_Properties_Price");
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

            // FK → User
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

            // Plan y créditos
            entity.Property(u => u.Plan).HasMaxLength(20).HasDefaultValue("base");
            entity.Property(u => u.Credits).HasDefaultValue(50);

            entity.HasIndex(u => u.Email).IsUnique().HasDatabaseName("IX_Users_Email");
            entity.HasIndex(u => u.GoogleId).HasDatabaseName("IX_Users_GoogleId");
        });
    }
}