using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
    public DbSet<DatasetColumn> DatasetColumns => Set<DatasetColumn>();
    public DbSet<DatasetRow> DatasetRows => Set<DatasetRow>();

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

        modelBuilder.Entity<DatasetColumn>(column =>
        {
            column.Property(c => c.Name).HasMaxLength(200);
            column.Property(c => c.Type).HasMaxLength(20);

            // Bir dataset silinince kolonları da silinsin.
            column.HasOne(c => c.Dataset)
                .WithMany()
                .HasForeignKey(c => c.DatasetId)
                .OnDelete(DeleteBehavior.Cascade);

            // İzolasyon Dataset üzerinden (RefreshToken'ın User üzerinden filtrelenmesi gibi).
            column.HasQueryFilter(c => c.Dataset.TenantId == _tenantContext.TenantId);
        });

        modelBuilder.Entity<DatasetRow>(row =>
        {
            // C# Dictionary  ⇄  JSON string. Kolon tipi gerçek jsonb olduğundan
            // Postgres tarafında data->>'ad' ile sorgulanabilir (G14 filtre motoru).
            var jsonOptions = new JsonSerializerOptions();
            var converter = new ValueConverter<Dictionary<string, object?>, string>(
                v => JsonSerializer.Serialize(v, jsonOptions),
                v => JsonSerializer.Deserialize<Dictionary<string, object?>>(v, jsonOptions)
                     ?? new Dictionary<string, object?>());

            // Dictionary mutable referans tip: EF'in değişiklik takibini doğru yapması için
            // içeriğe göre (structural) kıyaslayan bir ValueComparer şart, yoksa uyarı verir.
            var comparer = new ValueComparer<Dictionary<string, object?>>(
                (a, b) => JsonSerializer.Serialize(a, jsonOptions) == JsonSerializer.Serialize(b, jsonOptions),
                v => JsonSerializer.Serialize(v, jsonOptions).GetHashCode(),
                v => JsonSerializer.Deserialize<Dictionary<string, object?>>(
                         JsonSerializer.Serialize(v, jsonOptions), jsonOptions)!);

            row.Property(r => r.Data)
                .HasColumnType("jsonb")
                .HasConversion(converter, comparer);

            // Bir dataset silinince satırları da silinsin.
            row.HasOne(r => r.Dataset)
                .WithMany()
                .HasForeignKey(r => r.DatasetId)
                .OnDelete(DeleteBehavior.Cascade);

            // İzolasyon Dataset üzerinden (DatasetColumn ile aynı desen).
            row.HasQueryFilter(r => r.Dataset.TenantId == _tenantContext.TenantId);
        });
    }
}
