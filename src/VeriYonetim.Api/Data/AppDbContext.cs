using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Data;

public class AppDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Dataset> Datasets => Set<Dataset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.HasIndex(t => t.SchemaName).IsUnique();
            tenant.Property(t => t.Name).HasMaxLength(200);
            tenant.Property(t => t.Slug).HasMaxLength(100);
            tenant.Property(t => t.SchemaName).HasMaxLength(63); // PostgreSQL tanımlayıcı limiti
        });

        modelBuilder.Entity<User>(user =>
        {
            user.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            user.Property(u => u.Email).HasMaxLength(320);
            user.Property(u => u.Role).HasMaxLength(50);

            user.HasQueryFilter(u => u.TenantId == _tenantContext.TenantId);
        });

        modelBuilder.Entity<RefreshToken>(token =>
        {
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.Property(t => t.TokenHash).HasMaxLength(64);

            // User filtreli, RefreshToken filtresiz olamaz — refresh isteği token'sız
            // geldiğinden tenant context yok; sorgular bilinçli IgnoreQueryFilters kullanır.
            token.HasQueryFilter(t => t.User.TenantId == _tenantContext.TenantId);
        });

        modelBuilder.Entity<Dataset>(dataset =>
        {
            dataset.Property(d => d.Name).HasMaxLength(200);
            dataset.Property(d => d.Description).HasMaxLength(2000);

            // İzolasyon: her sorgu otomatik olarak sadece aktif tenant'ın setlerini görür.
            dataset.HasQueryFilter(d => d.TenantId == _tenantContext.TenantId);
        });
    }
}
