using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Sorunun dokunmadığı bir veri setinin bağdan düşürülmesi.
//
// Neden var? Model, katalogda ilişkili iki set görünce gereksiz yere bağ kurabiliyor.
// Ölçümde çıkan örnek: "kursu Excel ve Muhasebe dışında olan kayıtların adedi" sorusuna
// Katilimlar + Kursiyerler planı üretildi; oysa hem sayaç hem filtre Katilimlar'da.
//
// Bu fazlalık masum değil: bağ INNER JOIN kuruluyor, yani karşılığı olmayan satırlar düşer,
// birden çok karşılığı olanlar çoğalır. Sorgu hata vermez, sayı sessizce bozulur.
public class UnusedJoinTests
{
    private static readonly Guid KatilimlarId = Guid.NewGuid();
    private static readonly Guid KursiyerlerId = Guid.NewGuid();

    private static TenantCatalog Catalog(params string[] extraKatilimlarColumns)
    {
        var katilimlar = new Dictionary<string, string>
        {
            ["kayit_no"] = "text",
            ["kursiyer_kodu"] = "text",
            ["kurs"] = "text",
            ["sure_saat"] = "number",
            ["tarih"] = "date",
        };

        foreach (var column in extraKatilimlarColumns) katilimlar[column] = "number";

        return new TenantCatalog(
            new[]
            {
                new DatasetInfo(KatilimlarId, "Katilimlar", null, katilimlar),
                new DatasetInfo(KursiyerlerId, "Kursiyerler", null, new Dictionary<string, string>
                {
                    ["kod"] = "text",
                    ["ad_soyad"] = "text",
                    ["sehir"] = "text",
                    ["seviye"] = "text",
                }),
            },
            new[] { new RelationInfo(KatilimlarId, "kursiyer_kodu", KursiyerlerId, "kod") });
    }

    private static string[] Used(QueryScope scope) => scope.Sources.Select(s => s.Name).ToArray();

    [Fact]
    public void UnusedJoin_IsDropped()
    {
        // Ölçümde görülen hatanın kendisi: filtre de sayaç da Katilimlar'da.
        var scope = Catalog().BuildScope(
            new[] { "Katilimlar", "Kursiyerler" }, new[] { "kurs" });

        Assert.Equal(new[] { "Katilimlar" }, Used(scope));
    }

    [Fact]
    public void QualifiedReference_KeepsTheJoin()
    {
        // "kursiyerin seviyesine göre katılım sayısı" — bağ gerçekten gerekli.
        var scope = Catalog().BuildScope(
            new[] { "Katilimlar", "Kursiyerler" }, new[] { "Kursiyerler.seviye" });

        Assert.Equal(new[] { "Katilimlar", "Kursiyerler" }, Used(scope));
    }

    [Fact]
    public void BareReferenceOwnedOnlyByTheJoinedDataset_KeepsTheJoin()
    {
        // Nitelenmemiş "sehir" yalnız Kursiyerler'de var; niteliksiz diye düşürülemez.
        var scope = Catalog().BuildScope(
            new[] { "Katilimlar", "Kursiyerler" }, new[] { "sehir" });

        Assert.Equal(new[] { "Katilimlar", "Kursiyerler" }, Used(scope));
    }

    [Fact]
    public void NoColumnReferences_LeavesThePlanAlone()
    {
        // "{from:A, join:[B], metrics:[count]}" planında bağın kendisi sorunun anlamı
        // olabilir (B'de karşılığı olan A satırları). Bilgi yokken düzeltmeye kalkmak,
        // kapatmaya çalıştığımız hatayı ters yönde tekrarlamak olurdu.
        var scope = Catalog().BuildScope(
            new[] { "Katilimlar", "Kursiyerler" }, Array.Empty<string>());

        Assert.Equal(new[] { "Katilimlar", "Kursiyerler" }, Used(scope));
    }

    [Fact]
    public void MissingColumnList_LeavesThePlanAlone()
    {
        // Querystring uçları kolon listesi vermeden kapsam kuruyor; onların davranışı
        // değişmemeli.
        var scope = Catalog().BuildScope(new[] { "Katilimlar", "Kursiyerler" });

        Assert.Equal(new[] { "Katilimlar", "Kursiyerler" }, Used(scope));
    }

    [Fact]
    public void FromDataset_IsNeverDropped()
    {
        // Bütün referanslar öteki sette olsa bile sorunun BAŞLADIĞI set kalır: liste onun
        // satırlarıdır, bağ ona eklenir.
        var scope = Catalog().BuildScope(
            new[] { "Katilimlar", "Kursiyerler" }, new[] { "Kursiyerler.ad_soyad" });

        Assert.Equal(new[] { "Katilimlar", "Kursiyerler" }, Used(scope));
        Assert.Equal("Katilimlar", scope.Sources[0].Name);
    }

    [Fact]
    public void ColumnNameContainingDot_IsNotReadAsAQualifier()
    {
        // "2026.ciro" gerçek bir kolon adı olabilir; öndeki parça bilinen bir veri seti
        // değilse referans bölünmemeli (QueryScope.Resolve ile aynı okuma).
        var scope = Catalog("2026.ciro").BuildScope(
            new[] { "Katilimlar", "Kursiyerler" }, new[] { "2026.ciro" });

        Assert.Equal(new[] { "Katilimlar" }, Used(scope));
    }

    [Fact]
    public void UnknownDataset_StillReportsItsOwnError()
    {
        // Tanınmayan ad sessizce düşürülürse kullanıcı "böyle bir set yok" uyarısını
        // hiç görmez ve planın neden başka bir sonuç verdiğini anlayamaz.
        var error = Assert.Throws<InvalidQueryException>(() =>
            Catalog().BuildScope(new[] { "Katilimlar", "Egitmenler" }, new[] { "kurs" }));

        Assert.Contains("Egitmenler", error.Message);
    }

    [Fact]
    public void DroppingDoesNotFightAutomaticAddition()
    {
        // Eksik seti ekleyen kural ile gereksiz seti düşüren kural birbirini iptal etmemeli:
        // referansı olan set eklenir, referansı olmayan düşer.
        var scope = Catalog().BuildScope(new[] { "Katilimlar" }, new[] { "Kursiyerler.sehir" });

        Assert.Equal(new[] { "Katilimlar", "Kursiyerler" }, Used(scope));
    }
}
