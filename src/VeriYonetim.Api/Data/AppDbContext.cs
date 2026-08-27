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
    public DbSet<DatasetProfile> DatasetProfiles => Set<DatasetProfile>();
    public DbSet<DatasetIndex> DatasetIndexes => Set<DatasetIndex>();
    public DbSet<AskConversation> AskConversations => Set<AskConversation>();
    public DbSet<AskMessage> AskMessages => Set<AskMessage>();
    public DbSet<AccountToken> AccountTokens => Set<AccountToken>();
    public DbSet<DocumentJob> DocumentJobs => Set<DocumentJob>();
    public DbSet<DatasetWatch> DatasetWatches => Set<DatasetWatch>();
    public DbSet<DatasetWatchRun> DatasetWatchRuns => Set<DatasetWatchRun>();

    // Platform katmanı: tenant'ların üstünde durur, bu yüzden global query filter YOK.
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<PlatformAuditLog> PlatformAuditLogs => Set<PlatformAuditLog>();

    // Giriş denemesi sayacı. Bu da filtresiz: kayıt, henüz kimlik doğrulanmamışken —
    // yani tenant bağlamı yokken — yazılıyor (bkz. LoginAttempt).
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

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

        modelBuilder.Entity<LoginAttempt>(attempt =>
        {
            attempt.Property(a => a.Scope).HasMaxLength(20);
            attempt.Property(a => a.Email).HasMaxLength(320);

            // Kapı + e-posta başına TEK satır. Benzersizlik burada yalnız düzen değil
            // doğruluk meselesi: iki satır olsaydı sayaç ikiye bölünür ve sınır iki katına
            // çıkardı. Sorgular da hep bu iki alanla yapılıyor, yani indeks aynı zamanda
            // arama indeksi.
            attempt.HasIndex(a => new { a.Scope, a.Email }).IsUnique();

            // Bakım "şu tarihten eski" diye tarıyor.
            attempt.HasIndex(a => a.LastFailedAt);

            // NOT: sayaç artırımı bilerek EF üzerinden YAPILMIYOR. Oku-değiştir-yaz
            // biçiminde yazıldığında aynı anda gelen yirmi başarısız deneme aynı değeri
            // okuyup aynı değeri yazıyordu — yirmi deneme karşılığında sayaç bir artıyor,
            // beş denemelik sınır fiilen "beş TUR" sınırına dönüyordu. Artırım
            // LoginThrottle içinde tek bir atomik SQL ifadesine taşındı; oradaki yorumlara
            // bakın. (Eşzamanlılık damgası denendi ve ELENDİ: `xmin` PostgreSQL'in sistem
            // kolonu olduğu hâlde EF onu gerçek bir kolon sanıp AddColumn üretiyor.)
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

        modelBuilder.Entity<DatasetProfile>(profile =>
        {
            // Set başına tek profil: birincil anahtar setin kendisi.
            profile.HasKey(p => p.DatasetId);

            profile.Property(p => p.Json).HasColumnType("jsonb");

            // Set silinince profili de gitsin — önbellek verinin kendisi değil.
            profile.HasOne(p => p.Dataset)
                .WithMany()
                .HasForeignKey(p => p.DatasetId)
                .OnDelete(DeleteBehavior.Cascade);

            // İzolasyon Dataset üzerinden (DatasetColumn/DatasetRow ile aynı desen).
            profile.HasQueryFilter(p => p.Dataset.TenantId == _tenantContext.TenantId);
        });

        modelBuilder.Entity<DatasetIndex>(index =>
        {
            index.Property(i => i.ColumnName).HasMaxLength(200);
            index.Property(i => i.ColumnType).HasMaxLength(20);
            index.Property(i => i.IndexName).HasMaxLength(63); // PostgreSQL tanımlayıcı sınırı

            // Aynı kolon iki kez indekslenemesin.
            index.HasIndex(i => new { i.DatasetId, i.ColumnName }).IsUnique();

            // Set silinince kaydı da gitsin. Fiziksel indeks bundan etkilenmez: onu
            // referans sayımı yönetir (bkz. DatasetIndexService.DropIfUnusedAsync).
            // Cascade ile silinen kayıt o sayımın dışında kalır, yani kullanılmayan bir
            // indeks tabloda kalabilir — sessiz veri kaybından çok daha ucuz bir kusur.
            index.HasOne(i => i.Dataset)
                .WithMany()
                .HasForeignKey(i => i.DatasetId)
                .OnDelete(DeleteBehavior.Cascade);

            // İzolasyon Dataset üzerinden (DatasetColumn/DatasetRow ile aynı desen).
            index.HasQueryFilter(i => i.Dataset.TenantId == _tenantContext.TenantId);
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

        modelBuilder.Entity<AskConversation>(conversation =>
        {
            conversation.Property(c => c.Title).HasMaxLength(200);

            // Kullanıcı silinince sohbetleri de gitsin.
            conversation.HasOne(c => c.User)
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Liste en son konuşulana göre sıralandığı için indeks.
            conversation.HasIndex(c => new { c.UserId, c.UpdatedAt });

            // İzolasyon User üzerinden. Ayrıca uçlar kullanıcının KENDİ sohbetlerine
            // filtreler: sohbet kişiseldir, aynı firmadaki başkası göremez.
            conversation.HasQueryFilter(c => c.User.TenantId == _tenantContext.TenantId);
        });

        modelBuilder.Entity<AskMessage>(message =>
        {
            message.Property(m => m.Question).HasMaxLength(500);

            message.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            message.HasQueryFilter(m => m.Conversation.User.TenantId == _tenantContext.TenantId);
        });

        modelBuilder.Entity<DocumentJob>(job =>
        {
            job.Property(j => j.Kind).HasMaxLength(20);
            job.Property(j => j.Status).HasMaxLength(20);
            job.Property(j => j.ImageContentType).HasMaxLength(100);
            job.Property(j => j.FileName).HasMaxLength(260); // dosya adı sınırı

            // Sonuç JSON olarak duruyor; jsonb seçilmesinin sebebi ileride sorgulanabilmesi
            // değil, tipin ne olduğunu tabloda da görünür kılmak (satırlar da jsonb).
            job.Property(j => j.ResultJson).HasColumnType("jsonb");

            // Kullanıcı silinince işleri de gitsin: iş kaydı kişiye ait bir çalışma izidir.
            job.HasOne(j => j.User)
                .WithMany()
                .HasForeignKey(j => j.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Hedef set silinirse iş de anlamını yitirir (sonucu yazılacağı yer yok).
            job.HasOne(j => j.Dataset)
                .WithMany()
                .HasForeignKey(j => j.DatasetId)
                .OnDelete(DeleteBehavior.Cascade);

            // Liste "kullanıcının son işleri" biçiminde okunuyor.
            job.HasIndex(j => new { j.UserId, j.CreatedAt });

            // Temizlik işi bitmiş ve eskimiş kayıtları tarıyor.
            job.HasIndex(j => j.CreatedAt);

            // İzolasyon: iş kaydı doğrudan tenant taşıyor (Dataset deseni). Arka plan işi
            // bu alanı bilinçli olarak filtresiz okuyup bağlamı ondan kurar — yumurta-tavuk
            // sorununun tek kırıldığı yer orasıdır (bkz. DocumentJobRunner).
            job.HasQueryFilter(j => j.TenantId == _tenantContext.TenantId);
        });

        modelBuilder.Entity<DatasetWatch>(watch =>
        {
            watch.Property(w => w.Title).HasMaxLength(200);
            watch.Property(w => w.Question).HasMaxLength(500);
            watch.Property(w => w.Summary).HasMaxLength(1000);
            watch.Property(w => w.ConditionKind).HasMaxLength(20);
            watch.Property(w => w.ConditionOp).HasMaxLength(10);
            watch.Property(w => w.Status).HasMaxLength(20);

            // Plan JSON olarak duruyor; jsonb seçilmesinin sebebi DocumentJob.ResultJson
            // ile aynı: tipin ne olduğu tabloda da görünsün.
            watch.Property(w => w.PlanJson).HasColumnType("jsonb");

            // Kuran kullanıcı silinirse izleyici DÜŞMEZ, yalnız "kuran" alanı boşalır:
            // izleyici firmaya ait ve işten ayrılan birinin kurduğu alarmın onunla birlikte
            // sessizce yok olması, tam da bu adımın kapatmak istediği hâldir. Cascade
            // izleyiciyi silerdi, Restrict ise kullanıcının silinmesini engellerdi —
            // ikisi de yanlış cevap.
            watch.HasOne(w => w.CreatedByUser)
                .WithMany()
                .HasForeignKey(w => w.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Zamanlayıcı "süresi gelenler" diye tarıyor; tarama tenant ayrımı YAPMADAN
            // çalıştığı için indeks yalnız bu iki alan üzerinde anlamlı.
            watch.HasIndex(w => new { w.IsEnabled, w.NextRunAt });

            // İzolasyon: izleyici doğrudan tenant taşıyor (DocumentJob deseni). Arka plan
            // koşusu bu alanı filtresiz okuyup bağlamı ondan kurar.
            watch.HasQueryFilter(w => w.TenantId == _tenantContext.TenantId);
        });

        modelBuilder.Entity<DatasetWatchRun>(run =>
        {
            run.HasOne(r => r.Watch)
                .WithMany(w => w.Runs)
                .HasForeignKey(r => r.WatchId)
                .OnDelete(DeleteBehavior.Cascade);

            // Değer geçmişi "şu izleyicinin son N koşusu" biçiminde okunuyor.
            run.HasIndex(r => new { r.WatchId, r.RanAt });

            run.HasQueryFilter(r => r.Watch.TenantId == _tenantContext.TenantId);
        });
    }
}
