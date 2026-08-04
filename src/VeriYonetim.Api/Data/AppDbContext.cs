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
    public DbSet<DatasetRelation> DatasetRelations => Set<DatasetRelation>();
    public DbSet<AccountToken> AccountTokens => Set<AccountToken>();

    // Platform katmanı: tenant'ların üstünde durur, bu yüzden global query filter YOK.
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<PlatformAuditLog> PlatformAuditLogs => Set<PlatformAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.HasIndex(t => t.SchemaName).IsUnique();
            tenant.Property(t => t.Name).HasMaxLength(200);
            tenant.Property(t => t.Slug).HasMaxLength(100);
            tenant.Property(t => t.SchemaName).HasMaxLength(63); // PostgreSQL tanımlayıcı limiti

            // Varsayılan AÇIK. Bunu burada bildirmek şart: yalnızca C#'taki
            // "= true" başlatıcısı olsa migration üreticisi onu görmez ve kolonu
            // defaultValue: false ile ekler — bu da var olan tüm firmaları
            // askıya alınmış hâle düşürürdü.
            tenant.Property(t => t.IsActive).HasDefaultValue(true);
        });

        // Platform yöneticisi ve denetim kaydı BİLİNÇLİ olarak filtresizdir: bunlar
        // tenant'ların üstündeki işletmeci katmanı, bir tenant'a ait değiller.
        modelBuilder.Entity<PlatformAdmin>(admin =>
        {
            admin.HasIndex(a => a.Email).IsUnique();
            admin.Property(a => a.Email).HasMaxLength(320);
        });

        modelBuilder.Entity<PlatformAuditLog>(log =>
        {
            log.Property(l => l.PlatformAdminEmail).HasMaxLength(320);
            log.Property(l => l.Action).HasMaxLength(50);
            log.Property(l => l.TargetTenantName).HasMaxLength(200);

            // En yeni kayıt en üstte listelendiği için tarihe göre indeks.
            log.HasIndex(l => l.CreatedAt);
        });

        modelBuilder.Entity<User>(user =>
        {
            // E-posta global benzersiz: bir e-posta yalnızca tek bir tenant'a ait olabilir
            // (giriş yalnız e-posta+şifre ile yapılır, tenant bilgisi gerekmez).
            user.HasIndex(u => u.Email).IsUnique();
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

        modelBuilder.Entity<AccountToken>(token =>
        {
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.Property(t => t.TokenHash).HasMaxLength(64);
            token.Property(t => t.Purpose).HasMaxLength(20);
            token.Property(t => t.Email).HasMaxLength(320);
            token.Property(t => t.Role).HasMaxLength(50);

            // RefreshToken ile aynı gerekçe: token'ı doğrulayan istek GİRİŞ ÖNCESİ
            // gelir (davet edilen kişinin henüz oturumu yoktur), yani tenant context
            // yoktur. Filtre burada işe yaramaz; token'ın kendisi zaten hangi
            // tenant'a ait olduğunu taşır. Admin'in listeleme sorguları filtreye
            // ihtiyaç duyduğundan filtre yine de tanımlanır, doğrulama tarafı
            // bilinçli olarak IgnoreQueryFilters kullanır.
            token.HasQueryFilter(t => t.TenantId == _tenantContext.TenantId);
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

        modelBuilder.Entity<DatasetRelation>(relation =>
        {
            relation.Property(r => r.FromColumn).HasMaxLength(200);
            relation.Property(r => r.ToColumn).HasMaxLength(200);

            // İki uç da aynı Dataset tablosuna baktığından EF'e hangi tarafın hangisi
            // olduğu açıkça söylenmeli. Cascade YALNIZCA From tarafında: iki tarafa da
            // cascade konsaydı PostgreSQL "multiple cascade paths" hatası verirdi.
            relation.HasOne(r => r.FromDataset)
                .WithMany()
                .HasForeignKey(r => r.FromDatasetId)
                .OnDelete(DeleteBehavior.Cascade);

            relation.HasOne(r => r.ToDataset)
                .WithMany()
                .HasForeignKey(r => r.ToDatasetId)
                .OnDelete(DeleteBehavior.Restrict);

            // Aynı iki kolon arasında ikinci bir ilişki kurulamasın.
            relation.HasIndex(r => new { r.FromDatasetId, r.FromColumn, r.ToDatasetId, r.ToColumn })
                .IsUnique();

            // İzolasyon Dataset üzerinden (DatasetColumn/DatasetRow ile aynı desen).
            relation.HasQueryFilter(r => r.FromDataset.TenantId == _tenantContext.TenantId);
        });
    }
}
