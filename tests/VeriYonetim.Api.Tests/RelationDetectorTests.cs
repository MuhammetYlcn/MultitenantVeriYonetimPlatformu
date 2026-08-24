using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Veri setleri arasındaki bağın kendiliğinden bulunmasını doğrular.
//
// Burası iki yönden de kritik: BULAMAZSA kullanıcı setleri birlikte sorgulayamaz,
// YANLIŞ BULURSA sorgular sessizce yanlış sayı üretir. İkincisi çok daha kötü olduğu
// için testlerin çoğu "bulmaması gereken durumlar" üzerine.
public class RelationDetectorTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private Guid _tenantId;

    public RelationDetectorTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _tenantId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Tenants" ("Id", "Name", "Slug", "SchemaName", "CreatedAt", "IsActive")
            VALUES ({0}, 'DetectTest', 'detecttest', 'tenant_detect', now(), true)
            """, _tenantId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Bir veri seti kurar: kolonlar + satırlar. rows: kolon adı → değer dizisi.
    private async Task<Guid> SeedAsync(
        string name, IReadOnlyDictionary<string, string> columnTypes,
        IReadOnlyList<Dictionary<string, string>> rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var datasetId = Guid.NewGuid();

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Datasets" ("Id", "Name", "RowCount", "CreatedAt", "TenantId")
            VALUES ({0}, {1}, {2}, now(), {3})
            """, datasetId, name, rows.Count, _tenantId);

        var ordinal = 0;
        foreach (var (column, type) in columnTypes)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DatasetColumns" ("Id", "DatasetId", "Name", "Type", "Ordinal")
                VALUES ({0}, {1}, {2}, {3}, {4})
                """, Guid.NewGuid(), datasetId, column, type, ordinal++);
        }

        foreach (var row in rows)
        {
            var fields = row.Select(kv =>
            {
                // Sayısal kolonlar JSON'a tırnaksız yazılır (import ile aynı davranış).
                var quoted = columnTypes[kv.Key] == "number" ? kv.Value : $"\"{kv.Value}\"";
                return $"\"{kv.Key}\":{quoted}";
            });

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DatasetRows" ("Id", "Data", "DatasetId")
                VALUES ({0}, CAST({1} AS jsonb), {2})
                """, Guid.NewGuid(), $"{{{string.Join(",", fields)}}}", datasetId);
        }

        return datasetId;
    }

    // Algılayıcı, EF'in tenant query filter'ı üzerinden çalışır (izolasyon böyle sağlanır).
    // Filtre tenant kimliğini HTTP bağlamındaki claim'den okur; üretimde bu bağlam
    // istekle birlikte gelir. Testte de aynısını kurmalıyız, yoksa detector hiçbir veri
    // seti göremez — ki bu, testin sessizce "bulamadı" demesine yol açardı.
    private async Task<int> DetectAsync(Guid datasetId)
    {
        using var scope = _factory.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("tenant_id", _tenantId.ToString()) }, "Test"))
        };

        var detector = scope.ServiceProvider.GetRequiredService<IRelationDetector>();
        return await detector.DetectAsync(datasetId);
    }

    private async Task<List<(string FromColumn, string ToColumn, bool Auto)>> RelationsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.DatasetRelations
            .IgnoreQueryFilters()
            .Select(r => new ValueTuple<string, string, bool>(
                r.FromColumn, r.ToColumn, r.IsAutoDetected))
            .ToListAsync();
    }

    private static Dictionary<string, string> Row(params (string, string)[] fields) =>
        fields.ToDictionary(f => f.Item1, f => f.Item2);

    // --- bulması gereken durum --------------------------------------------------------

    [Fact]
    public async Task DetectsForeignKey_EvenWhenColumnNamesDiffer()
    {
        // "musteri_no" ile "no" birbirine hiç benzemiyor; karar ada değil VERİYE bakıyor.
        await SeedAsync("Musteriler",
            new Dictionary<string, string> { ["no"] = "text", ["ad"] = "text" },
            new[]
            {
                Row(("no", "M1"), ("ad", "Ali")),
                Row(("no", "M2"), ("ad", "Ayse")),
                Row(("no", "M3"), ("ad", "Veli")),
            });

        var satislar = await SeedAsync("Satislar",
            new Dictionary<string, string> { ["musteri_no"] = "text", ["tutar"] = "number" },
            new[]
            {
                Row(("musteri_no", "M1"), ("tutar", "100")),
                Row(("musteri_no", "M1"), ("tutar", "150")),
                Row(("musteri_no", "M2"), ("tutar", "200")),
            });

        Assert.Equal(1, await DetectAsync(satislar));

        var relation = Assert.Single(await RelationsAsync());
        Assert.Equal("musteri_no", relation.FromColumn);
        Assert.Equal("no", relation.ToColumn);
        Assert.True(relation.Auto);   // makine işi olduğu kayda geçiyor
    }

    // --- bulmaması gereken durumlar ---------------------------------------------------

    [Fact]
    public async Task DoesNotLink_WhenNeitherSideIsUnique()
    {
        // Benzersiz olmayan kolon anahtar olamaz. Yine de bağlansaydı birleştirme
        // satırları ÇOĞALTIR ve toplamlar sessizce şişerdi.
        //
        // İki tarafta da mükerrer değer var: algılama iki yönü de denediği için
        // (bkz. DetectAsync) tek tarafı benzersiz bırakmak bu kuralı değil, öbür yönü
        // ölçerdi.
        await SeedAsync("Sehirler",
            new Dictionary<string, string> { ["sehir"] = "text" },
            new[]
            {
                Row(("sehir", "Ankara")),
                Row(("sehir", "Ankara")),
                Row(("sehir", "Izmir")),
            });

        var satislar = await SeedAsync("Satislar",
            new Dictionary<string, string> { ["sehir"] = "text" },
            new[]
            {
                Row(("sehir", "Ankara")),
                Row(("sehir", "Ankara")),
                Row(("sehir", "Izmir")),
            });

        Assert.Equal(0, await DetectAsync(satislar));
        Assert.Empty(await RelationsAsync());
    }

    [Fact]
    public async Task DoesNotLink_WhenCoverageIsLow()
    {
        // Değerlerin yarısı hedefte yoksa bu bir yabancı anahtar değildir.
        await SeedAsync("Kodlar",
            new Dictionary<string, string> { ["kod"] = "text" },
            new[] { Row(("kod", "A")), Row(("kod", "B")), Row(("kod", "C")) });

        var digeri = await SeedAsync("Digeri",
            new Dictionary<string, string> { ["kod"] = "text" },
            new[] { Row(("kod", "A")), Row(("kod", "X")), Row(("kod", "Y")), Row(("kod", "Z")) });

        Assert.Equal(0, await DetectAsync(digeri));
    }

    [Fact]
    public async Task DoesNotLink_AcrossDifferentTypes()
    {
        // Metin kolonu sayısal bir anahtara bağlanmaz; değerler yazı olarak örtüşse bile.
        await SeedAsync("Sayilar",
            new Dictionary<string, string> { ["kod"] = "number" },
            new[] { Row(("kod", "1")), Row(("kod", "2")), Row(("kod", "3")) });

        var metinler = await SeedAsync("Metinler",
            new Dictionary<string, string> { ["kod"] = "text" },
            new[] { Row(("kod", "1")), Row(("kod", "2")) });

        Assert.Equal(0, await DetectAsync(metinler));
    }

    [Fact]
    public async Task DoesNotLink_WhenColumnHasTooFewDistinctValues()
    {
        // Tek değerli kolonlar tesadüfen örtüşür ("TR" = "TR"); anlamlı bir anahtar değil.
        await SeedAsync("Ulkeler",
            new Dictionary<string, string> { ["ulke"] = "text" },
            new[] { Row(("ulke", "TR")) });

        var kayitlar = await SeedAsync("Kayitlar",
            new Dictionary<string, string> { ["ulke"] = "text" },
            new[] { Row(("ulke", "TR")), Row(("ulke", "TR")) });

        Assert.Equal(0, await DetectAsync(kayitlar));
    }

    [Fact]
    public async Task DoesNotAddSecondLink_WhenPairIsAlreadyRelated()
    {
        // Aynı iki set arasında ikinci bir bağ gürültüdür; ilki (elle ya da otomatik) yeter.
        await SeedAsync("Musteriler",
            new Dictionary<string, string> { ["no"] = "text", ["kod"] = "text" },
            new[]
            {
                Row(("no", "M1"), ("kod", "K1")),
                Row(("no", "M2"), ("kod", "K2")),
                Row(("no", "M3"), ("kod", "K3")),
            });

        var satislar = await SeedAsync("Satislar",
            new Dictionary<string, string> { ["musteri_no"] = "text", ["musteri_kod"] = "text" },
            new[]
            {
                Row(("musteri_no", "M1"), ("musteri_kod", "K1")),
                Row(("musteri_no", "M2"), ("musteri_kod", "K2")),
            });

        Assert.Equal(1, await DetectAsync(satislar));

        // İkinci çalıştırma yeni bağ eklememeli.
        Assert.Equal(0, await DetectAsync(satislar));
        Assert.Single(await RelationsAsync());
    }

    [Fact]
    public async Task DetectsNothing_WhenThereIsOnlyOneDataset()
    {
        var only = await SeedAsync("Tek",
            new Dictionary<string, string> { ["kod"] = "text" },
            new[] { Row(("kod", "A")), Row(("kod", "B")) });

        Assert.Equal(0, await DetectAsync(only));
    }

    [Fact]
    public async Task DetectsForeignKey_WhenKeyIsInTheNewlyImportedDataset()
    {
        // Bağın bulunması, dosyaların YÜKLENME SIRASINA bağlı olmamalı. Burada anahtar
        // (benzersiz kolon) sonradan yüklenen sette: önce satışlar geldi, müşteri listesi
        // sonra. Yalnız tek yön denenseydi bu bağ hiç kurulmazdı ve kullanıcı iki seti
        // birlikte sorgulayamazdı — üstelik sebebini de göremezdi.
        await SeedAsync("Satislar",
            new Dictionary<string, string> { ["musteri_no"] = "text", ["tutar"] = "number" },
            new[]
            {
                Row(("musteri_no", "M1"), ("tutar", "100")),
                Row(("musteri_no", "M1"), ("tutar", "150")),
                Row(("musteri_no", "M2"), ("tutar", "200")),
            });

        var musteriler = await SeedAsync("Musteriler",
            new Dictionary<string, string> { ["no"] = "text", ["ad"] = "text" },
            new[]
            {
                Row(("no", "M1"), ("ad", "Ali")),
                Row(("no", "M2"), ("ad", "Ayse")),
                Row(("no", "M3"), ("ad", "Veli")),
            });

        Assert.Equal(1, await DetectAsync(musteriler));

        // Yön veriye göre kurulmalı: yabancı anahtarı taşıyan taraf "From", anahtarın
        // bulunduğu taraf "To". Ters kaydedilseydi birleştirme satırları çoğaltırdı.
        var relation = Assert.Single(await RelationsAsync());
        Assert.Equal("musteri_no", relation.FromColumn);
        Assert.Equal("no", relation.ToColumn);
        Assert.True(relation.Auto);
    }

    // --- profil önbelleği -------------------------------------------------------------
    //
    // 21.08 ölçümünde içe aktarmanın sabit ~5 saniyesinin, hiç değişmemiş komşu setlerin
    // her seferinde baştan profillenmesinden geldiği görüldü. Aşağıdaki testler önbelleğin
    // hem işe yaradığını (yeniden ölçmüyor) hem de DOĞRU olduğunu (veri değişince
    // bayat profile güvenmiyor) doğruluyor. İkincisi kritik: bayat bir profil, olmayan
    // bir ilişkiyi var gösterir ve sorgular sessizce yanlış sayı üretir.

    [Fact]
    public async Task ReusesCachedProfile_WhenDatasetHasNotChanged()
    {
        await SeedAsync("Musteriler",
            new Dictionary<string, string> { ["no"] = "text" },
            new[] { Row(("no", "M1")), Row(("no", "M2")), Row(("no", "M3")) });

        var satislar = await SeedAsync("Satislar",
            new Dictionary<string, string> { ["musteri_no"] = "text" },
            new[] { Row(("musteri_no", "M1")), Row(("musteri_no", "M2")) });

        await DetectAsync(satislar);

        var first = await ProfilesAsync();
        Assert.Equal(2, first.Count); // hem kaynak hem komşu profillendi

        // Üçüncü bir set geliyor: iki eski set değişmediğine göre yeniden ölçülmemeli.
        var urunler = await SeedAsync("Urunler",
            new Dictionary<string, string> { ["kod"] = "text" },
            new[] { Row(("kod", "U1")), Row(("kod", "U2")) });

        await DetectAsync(urunler);

        var second = await ProfilesAsync();

        foreach (var (datasetId, computedAt) in first)
            Assert.Equal(computedAt, second[datasetId]);
    }

    [Fact]
    public async Task RecomputesProfile_WhenDatasetChanged_AndDoesNotTrustStaleUniqueness()
    {
        // Musteriler.no başlangıçta benzersiz — anahtar olmaya elverişli.
        var musteriler = await SeedAsync("Musteriler",
            new Dictionary<string, string> { ["no"] = "text" },
            new[] { Row(("no", "M1")), Row(("no", "M2")), Row(("no", "M3")) });

        // Satışlarda aynı müşteri iki kez geçiyor: bu kolon benzersiz değil, yani başka
        // bir setin ona bağlanması mümkün değil. Testin ölçtüğü şey Müşteriler'in
        // profili olmalı, araya girecek ikinci bir aday değil.
        var satislar = await SeedAsync("Satislar",
            new Dictionary<string, string> { ["musteri_no"] = "text" },
            new[]
            {
                Row(("musteri_no", "M1")),
                Row(("musteri_no", "M1")),
                Row(("musteri_no", "M2")),
            });

        Assert.Equal(1, await DetectAsync(satislar));

        // Artık DEĞİL: mükerrer bir "M1" girdi, kolon anahtar olmaktan çıktı.
        await AppendRowAsync(musteriler, """{"no":"M1"}""");

        // Siparişlerde de mükerrer değer var: yeni set benzersiz bırakılsaydı komşulardan
        // ONA doğru bir bağ kurulabilirdi (algılama iki yönü de dener) ve test ölçmek
        // istediği şeyi değil o yolu ölçerdi.
        var siparisler = await SeedAsync("Siparisler",
            new Dictionary<string, string> { ["musteri_no"] = "text" },
            new[]
            {
                Row(("musteri_no", "M1")),
                Row(("musteri_no", "M1")),
                Row(("musteri_no", "M2")),
            });

        // Önbellekteki eski profile güvenilseydi burada bir bağ kurulurdu ve
        // Siparişler ile Müşteriler birleştirildiğinde satırlar çoğalırdı.
        Assert.Equal(0, await DetectAsync(siparisler));
        Assert.Single(await RelationsAsync());
    }

    [Fact]
    public async Task FallsBackToMeasuring_WhenCachedProfileIsUnreadable()
    {
        // Önbellek bir kolaylıktır: bozuksa iş durmaz, yalnız yavaşlar.
        var musteriler = await SeedAsync("Musteriler",
            new Dictionary<string, string> { ["no"] = "text" },
            new[] { Row(("no", "M1")), Row(("no", "M2")), Row(("no", "M3")) });

        // Satışlarda mükerrer değer var: bu set hedef anahtar olamaz, ölçülen tek aday
        // Müşteriler kalır.
        var satislar = await SeedAsync("Satislar",
            new Dictionary<string, string> { ["musteri_no"] = "text" },
            new[]
            {
                Row(("musteri_no", "M1")),
                Row(("musteri_no", "M1")),
                Row(("musteri_no", "M2")),
            });

        await DetectAsync(satislar);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "DatasetProfiles" SET "Json" = CAST({0} AS jsonb) WHERE "DatasetId" = {1}""",
                """{"bozuk":true}""", musteriler);
        }

        // Siparişlerde de mükerrer değer var: yeni set benzersiz bırakılsaydı komşulardan
        // ONA doğru bir bağ kurulabilirdi (algılama iki yönü de dener) ve test ölçmek
        // istediği şeyi değil o yolu ölçerdi.
        var siparisler = await SeedAsync("Siparisler",
            new Dictionary<string, string> { ["musteri_no"] = "text" },
            new[]
            {
                Row(("musteri_no", "M1")),
                Row(("musteri_no", "M1")),
                Row(("musteri_no", "M2")),
            });

        Assert.Equal(1, await DetectAsync(siparisler));
    }

    private async Task<Dictionary<Guid, DateTime>> ProfilesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.DatasetProfiles
            .IgnoreQueryFilters()
            .ToDictionaryAsync(p => p.DatasetId, p => p.ComputedAt);
    }

    // Sete tek satır ekler ve setin damgasını ilerletir — satır yazan uçların yaptığının
    // aynısı (bkz. DatasetProfile.Stamp).
    private async Task AppendRowAsync(Guid datasetId, string json)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "DatasetRows" ("Id", "Data", "DatasetId")
            VALUES ({0}, CAST({1} AS jsonb), {2});
            UPDATE "Datasets"
            SET "RowCount" = "RowCount" + 1, "UpdatedAt" = now()
            WHERE "Id" = {2};
            """, Guid.NewGuid(), json, datasetId);
    }
}
