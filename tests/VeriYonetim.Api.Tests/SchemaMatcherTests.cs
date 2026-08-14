using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Keşif geçişinden çıkan taslak şemanın var olan veri setleriyle eşleştirilmesi.
//
// Buradaki testlerin çoğu "eşleşmeli" değil "EŞLEŞMEMELİ" diyor. Sebebi RelationDetector'la
// aynı: yanlış sete yazılmış bir belge sessizce yanlış toplam üretir, yeni set açmanın
// bedeliyse yalnız fazladan bir karttır. Eşiğin altında kalmak bir kusur değil, tasarım.
public class SchemaMatcherTests
{
    private static DatasetSchema Set(string name, params (string Name, string Type)[] columns) =>
        new(Guid.NewGuid(), name,
            columns.Select(c => new ColumnSchema(c.Name, c.Type)).ToList());

    private static IReadOnlyList<ColumnSchema> Belge(params (string Name, string Type)[] columns) =>
        columns.Select(c => new ColumnSchema(c.Name, c.Type)).ToList();

    [Fact(DisplayName = "Eşleme: birebir aynı kolonlar tam puan alır")]
    public void AyniKolonlarTamPuan()
    {
        var hedef = Set("Faturalar",
            ("fatura_no", "text"), ("tarih", "date"), ("tutar", "number"));
        var belge = Belge(("fatura_no", "text"), ("tarih", "date"), ("tutar", "number"));

        var match = SchemaMatcher.Score(belge, hedef);

        Assert.Equal(1.0, match.Score);
        Assert.Equal(3, match.Mappings.Count);
        Assert.Empty(match.MissingColumns);
        Assert.Empty(match.ExtraColumns);
        Assert.All(match.Mappings, m => Assert.False(m.TypeConflict));
    }

    [Fact(DisplayName = "Eşleme: Türkçe yazım farkı (büyük harf, iyelik eki, ayraç) engel değildir")]
    public void TurkceYazimFarkiEngelDegil()
    {
        // Sol taraf CSV başlığından gelmiş bir set, sağ taraf modelin belgeden okuduğu adlar.
        var hedef = Set("Satışlar",
            ("Ürün Adı", "text"), ("Tutar", "number"), ("Fatura No", "text"));
        var belge = Belge(("urun_adi", "text"), ("tutari", "number"), ("fatura_no", "text"));

        var match = SchemaMatcher.Score(belge, hedef);

        Assert.Equal(1.0, match.Score);
        Assert.Equal(3, match.Mappings.Count);
    }

    [Fact(DisplayName = "Eşleme: nitelenmiş ad kısa adla eşleşir ama tam puan almaz")]
    public void AltKumeAdEslesirAmaTamPuanDegil()
    {
        var hedef = Set("Faturalar", ("fatura_tarihi", "date"), ("fatura_tutari", "number"));
        var belge = Belge(("tarih", "date"), ("tutar", "number"));

        var match = SchemaMatcher.Score(belge, hedef);

        Assert.Equal(2, match.Mappings.Count);
        Assert.Equal(0.7, match.Score);          // 2 * (0,7 + 0,7) / (2 + 2)
        Assert.Contains(match.Mappings, m => m.Discovered == "tarih" && m.Target == "fatura_tarihi");
    }

    [Fact(DisplayName = "Eşleme: alakasız set ÖNERİLMEZ — liste boş döner (yeni set demektir)")]
    public void AlakasizSetOnerilmez()
    {
        var hedef = Set("Personel",
            ("ad", "text"), ("soyad", "text"), ("departman", "text"), ("maas", "number"));
        var belge = Belge(("fatura_no", "text"), ("tarih", "date"), ("tutar", "number"));

        var matches = SchemaMatcher.Match(belge, new[] { hedef });

        Assert.Empty(matches);
    }

    [Fact(DisplayName = "Eşleme: tek kolonun tutması bir seti seçtirmez (her belgede tarih vardır)")]
    public void TekKolonYetmez()
    {
        var hedef = Set("Toplantılar", ("tarih", "date"), ("konu", "text"), ("katilimci", "text"));
        var belge = Belge(("tarih", "date"), ("fatura_no", "text"), ("tutar", "number"));

        Assert.Empty(SchemaMatcher.Match(belge, new[] { hedef }));
    }

    [Fact(DisplayName = "Eşleme: tip uyuşmazlığı eşlemeyi bozmaz ama İŞARETLENİR ve puanı düşürür")]
    public void TipUyusmazligiIsaretlenir()
    {
        // Belgede tarih metin okundu ("31 Ağustos 2026" gibi), sette tarih kolonu.
        var hedef = Set("Faturalar", ("fatura_no", "text"), ("tarih", "date"));
        var belge = Belge(("fatura_no", "text"), ("tarih", "text"));

        var match = SchemaMatcher.Score(belge, hedef);

        var çakışan = Assert.Single(match.Mappings.Where(m => m.TypeConflict));
        Assert.Equal("tarih", çakışan.Target);

        // 2 * (1 + 0,5) / 4 = 0,75 — eşleşme duruyor, ama tam sayılmıyor.
        Assert.Equal(0.75, match.Score);
    }

    [Fact(DisplayName = "Eşleme: metin kolonu her tipi kabul eder (hedef text ise çakışma yok)")]
    public void MetinHedefCakismaz()
    {
        var hedef = Set("Kayıtlar", ("kod", "text"), ("aciklama", "text"));
        var belge = Belge(("kod", "number"), ("aciklama", "text"));

        var match = SchemaMatcher.Score(belge, hedef);

        Assert.All(match.Mappings, m => Assert.False(m.TypeConflict));
    }

    [Fact(DisplayName = "Eşleme: eksik ve fazla kolonlar ayrı ayrı raporlanır")]
    public void EksikVeFazlaKolonlarRaporlanir()
    {
        var hedef = Set("Faturalar",
            ("fatura_no", "text"), ("tarih", "date"), ("tutar", "number"), ("vergi_no", "text"));
        var belge = Belge(
            ("fatura_no", "text"), ("tarih", "date"), ("tutar", "number"), ("kdv_orani", "number"));

        var match = SchemaMatcher.Score(belge, hedef);

        Assert.Equal(new[] { "vergi_no" }, match.MissingColumns);   // sette var, belgede yok
        Assert.Equal(new[] { "kdv_orani" }, match.ExtraColumns);    // belgede var, sette yok
    }

    [Fact(DisplayName = "Eşleme: geniş bir set, küçük belgeyi 'tam eşleşme' gibi göstermez")]
    public void GenisSetKucukBelgeyiYutmaz()
    {
        // 12 kolonluk bir sette belgedeki 3 kolonun hepsi bulunuyor. Tek yönlü kapsama
        // ölçseydik puan 1,0 çıkardı ve bu set HER belgeyi kendine çekerdi.
        var kolonlar = new[] { "fatura_no", "tarih", "tutar" }
            .Concat(Enumerable.Range(1, 9).Select(i => $"alan_{i}"))
            .Select(a => (a, a == "tutar" ? "number" : "text"))
            .ToArray();

        var match = SchemaMatcher.Score(
            Belge(("fatura_no", "text"), ("tarih", "text"), ("tutar", "number")),
            Set("Her Şey", kolonlar));

        Assert.Equal(3, match.Mappings.Count);
        Assert.True(match.Score < SchemaMatcher.MinScore,
            $"geniş set eşiği geçmemeliydi: {match.Score}");
    }

    [Fact(DisplayName = "Eşleme: adaylar puana göre sıralanır, en iyisi başta")]
    public void AdaylarSiralanir()
    {
        var tam = Set("Faturalar",
            ("fatura_no", "text"), ("tarih", "date"), ("tutar", "number"));
        var kismi = Set("Giderler",
            ("belge_no", "text"), ("tarih", "date"), ("tutar", "number"));

        var belge = Belge(("fatura_no", "text"), ("tarih", "date"), ("tutar", "number"));

        var matches = SchemaMatcher.Match(belge, new[] { kismi, tam });

        Assert.Equal(2, matches.Count);
        Assert.Equal("Faturalar", matches[0].Name);
        Assert.True(matches[0].Score > matches[1].Score);
    }

    [Fact(DisplayName = "Eşleme: bir hedef kolon iki belge kolonuna birden bağlanmaz")]
    public void HedefKolonBirKezKullanilir()
    {
        // Belgede iki ayrı tarih var; sette yalnız bir tarih kolonu. İkisi de aynı kolona
        // bağlanırsa kullanıcıya "her şey oturdu" gibi görünür, oysa biri kaybolacaktır.
        var hedef = Set("Faturalar", ("fatura_tarihi", "date"), ("tutar", "number"));
        var belge = Belge(("tarih", "date"), ("vade_tarihi", "date"), ("tutar", "number"));

        var match = SchemaMatcher.Score(belge, hedef);

        Assert.Equal(2, match.Mappings.Count);
        Assert.Single(match.Mappings.Where(m => m.Target == "fatura_tarihi"));
        Assert.Single(match.ExtraColumns);
    }

    [Theory(DisplayName = "Eşleme: ad çözümlemesi ayraç, büyük harf ve iyelik ekini eler")]
    [InlineData("fatura_no", "Fatura No", 1.0)]
    [InlineData("urun_adi", "ÜRÜN ADI", 1.0)]
    [InlineData("tutari", "tutar", 1.0)]
    [InlineData("tarih", "fatura_tarihi", 0.7)]
    [InlineData("musteri", "tedarikci", 0.0)]
    [InlineData("kdv", "kdv_orani", 0.7)]
    public void AdPuani(string sol, string sag, double beklenen) =>
        Assert.Equal(beklenen, SchemaMatcher.NameScore(sol, sag));

    [Fact(DisplayName = "Eşleme: kısa adlar iyelik eki sanılıp bozulmaz (no, ad, kdv)")]
    public void KisaAdlarKorunur()
    {
        Assert.Equal(new[] { "no" }, SchemaMatcher.Tokens("no"));
        Assert.Equal(new[] { "ad" }, SchemaMatcher.Tokens("ad"));
        Assert.Equal(new[] { "kdv" }, SchemaMatcher.Tokens("KDV"));
    }

    [Fact(DisplayName = "Eşleme: belgeden hiç kolon çıkmadıysa aday aranmaz")]
    public void KolonsuzBelgeAdayAramaz() =>
        Assert.Empty(SchemaMatcher.Match(
            Array.Empty<ColumnSchema>(),
            new[] { Set("Faturalar", ("fatura_no", "text"), ("tutar", "number")) }));
}
