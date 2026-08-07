using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Satır listesine kaydı tanıtan kolonun eklenmesi.
//
// Neden var? "maaşı en yüksek 5 personel hangileri" sorusunun cevabı isimlerdir, ama soru
// hangi kolonların gösterileceğini söylemez — model de tutarlı bir seçim yapamaz. Kural
// sunucuda: liste birden çok kolon gösteriyorsa ve içinde tanıtıcı bir kolon yoksa, sorunun
// başladığı veri setinin ad/kod kolonu başa eklenir.
public class RowSelectIdentityTests
{
    private static readonly Dictionary<string, string> Personel = new()
    {
        ["sicil"] = "text",
        ["ad_soyad"] = "text",
        ["departman"] = "text",
        ["sehir"] = "text",
        ["maas"] = "number",
    };

    private static QueryScope Scope(IReadOnlyDictionary<string, string>? columns = null) =>
        QueryScope.Single(columns ?? Personel);

    [Fact]
    public void ListWithoutIdentity_GetsNameColumnFirst()
    {
        var result = QueryPlanMapper.WithIdentityColumn(new[] { "sehir", "maas" }, Scope());

        Assert.Equal(new[] { "ad_soyad", "sehir", "maas" }, result);
    }

    [Fact]
    public void NameIsPreferredOverCode()
    {
        // Şemada hem "sicil" hem "ad_soyad" var. "P-1042" kullanıcıya kimin kastedildiğini
        // söylemez; tercih sırası ad kolonundan yana.
        var result = QueryPlanMapper.WithIdentityColumn(new[] { "departman", "maas" }, Scope());

        Assert.Equal("ad_soyad", result![0]);
    }

    [Fact]
    public void ListThatAlreadyIdentifies_IsUntouched()
    {
        var select = new[] { "ad_soyad", "maas" };

        Assert.Same(select, QueryPlanMapper.WithIdentityColumn(select, Scope()));
    }

    [Fact]
    public void SingleColumnAnswer_IsUntouched()
    {
        // "en son işe giriş ne zaman" — cevap tek bir değerdir. Yanına isim eklemek
        // sorulmayan bir şeyi cevaba karıştırmak olurdu.
        var select = new[] { "ise_giris" };

        Assert.Same(select, QueryPlanMapper.WithIdentityColumn(select, Scope()));
    }

    [Fact]
    public void EmptySelect_IsUntouched()
    {
        // Boş seçim "bütün kolonlar" demek; tanıtıcı kolon zaten içinde.
        Assert.Null(QueryPlanMapper.WithIdentityColumn(null, Scope()));
        Assert.Empty(QueryPlanMapper.WithIdentityColumn(Array.Empty<string>(), Scope())!);
    }

    [Fact]
    public void SchemaWithoutIdentityColumn_IsUntouched()
    {
        // Tanıtıcı kolonu olmayan bir sette uydurulacak bir şey yok.
        var olcumler = new Dictionary<string, string>
        {
            ["tarih"] = "date",
            ["sicaklik"] = "number",
            ["nem"] = "number",
        };

        var select = new[] { "sicaklik", "nem" };

        Assert.Same(select, QueryPlanMapper.WithIdentityColumn(select, Scope(olcumler)));
    }

    [Fact]
    public void MultiSourceQuery_TakesIdentityFromTheFirstSource()
    {
        // Liste "from" setinin satırlarıdır; join'e katılan setin tanıtıcısı yanıltıcı olur.
        var siparisler = new QuerySource("d0", Guid.NewGuid(), "Siparisler",
            new Dictionary<string, string>
            {
                ["urun_adi"] = "text",
                ["musteri_no"] = "text",
                ["tutar"] = "number",
            });

        var musteriler = new QuerySource("d1", Guid.NewGuid(), "Musteriler",
            new Dictionary<string, string>
            {
                ["no"] = "text",
                ["ad"] = "text",
                ["sehir"] = "text",
            });

        var scope = new QueryScope(new[] { siparisler, musteriler },
            new[] { new QueryJoin("d0", "musteri_no", "d1", "no") });

        var result = QueryPlanMapper.WithIdentityColumn(
            new[] { "Musteriler.sehir", "Siparisler.tutar" }, scope);

        // Çok kaynakta ad nitelikli yazılmalı, yoksa çözümleme belirsiz kalır.
        Assert.Equal("Siparisler.urun_adi", result![0]);
    }

    [Fact]
    public void QualifiedIdentityInSelect_IsRecognized()
    {
        // "Musteriler.ad" zaten tanıtıcıdır; nitelik öneki bunu gizlememeli.
        var siparisler = new QuerySource("d0", Guid.NewGuid(), "Siparisler",
            new Dictionary<string, string> { ["urun_adi"] = "text", ["tutar"] = "number" });

        var musteriler = new QuerySource("d1", Guid.NewGuid(), "Musteriler",
            new Dictionary<string, string> { ["ad"] = "text", ["sehir"] = "text" });

        var scope = new QueryScope(new[] { siparisler, musteriler });

        var select = new[] { "Musteriler.ad", "Siparisler.tutar" };

        Assert.Same(select, QueryPlanMapper.WithIdentityColumn(select, scope));
    }
}
