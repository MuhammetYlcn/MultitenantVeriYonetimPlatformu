using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Görsel modelin (qwen2.5vl:7b) belge çıkarımının sunucu tarafında ayrıştırılması.
//
// Buradaki metinlerin çoğu UYDURMA DEĞİL: 2026-08-10 ölçümünde modelin gerçekten döndürdüğü
// yanıtlardan alındı. Sebebi şu: bu katmanın işi "doğru JSON"u okumak değil, modelin
// gerçekte ne döndürdüğünü okumak. Kurallar gözlenmiş davranışa karşı yazıldı, testleri de
// o gözlemi tutuyor — model değişirse burada kırılır ve haber verir.
public class DocumentExtractionTests
{
    // Ölçümde 3/3 belgede görülen davranış: istenen {"alanlar": {...}} sarmalayıcısı atlanıp
    // alanlar en üste konuyor. Reddetmek çalışan bir çıkarımı biçim yüzünden çöpe atmak olurdu.
    private const string DuzYanit = """
        {
          "satici": "Çağdaş İnşaat Taahhüt Ltd. Şti.",
          "alici": "Anadolu Yapı Market",
          "tarih": "2026-01-01",
          "belge_no": "A329258",
          "ara_toplam": "14.849,57",
          "kdv_orani": "1",
          "genel_toplam": "14.998,07",
          "kalemler": [
            { "urun": "Toz şeker 50 kg", "adet": "1", "birim_fiyat": "721,83", "tutar": "721.83" },
            { "urun": "Çimento torba 50 kg", "adet": "3", "birim_fiyat": "664,44", "tutar": "1993.32" }
          ]
        }
        """;

    // Gider pusulasında görülen uydurma: açıklama ve net tutar bir "kalem" satırı yapıldı.
    // İkisi de belge düzeyinde zaten var, yani satır hiçbir yeni bilgi taşımıyor.
    private const string UydurmaKalemliYanit = """
        ```json
        {
          "alanlar": {
            "odenen": "Şahin Otomotiv Yedek Parça",
            "tarih": "2025-01-15",
            "belge_no": "GP-2695",
            "aciklama": "Ofis temizlik hizmeti",
            "net_tutar": 47158.91
          },
          "kalemler": [
            { "urun": "Ofis temizlik hizmeti", "birim": null, "adet": null,
              "birim_fiyat": null, "tutar": 47158.91 }
          ]
        }
        ```
        """;

    // ------------------------------ yapı ------------------------------

    [Fact]
    public void FlattenedResponse_IsAccepted_AndReported()
    {
        var belge = DocumentExtractionParser.Parse(DuzYanit)!;

        Assert.Equal("Anadolu Yapı Market", belge.Fields["alici"]);
        Assert.Equal(2, belge.Items.Count);
        Assert.Contains(belge.Notes, n => n.Contains("alanlar"));
    }

    [Fact]
    public void WrappedResponse_InsideCodeFence_IsParsed()
    {
        var belge = DocumentExtractionParser.Parse(UydurmaKalemliYanit)!;

        Assert.Equal("GP-2695", belge.Fields["belge_no"]);
        Assert.Equal("Şahin Otomotiv Yedek Parça", belge.Fields["odenen"]);
    }

    [Fact]
    public void UnparseableResponse_ReturnsNull()
    {
        // "Belge okunamadı" demek, boş bir sonuç uydurmaktan iyidir.
        Assert.Null(DocumentExtractionParser.Parse("Bu görüntüde bir belge göremedim."));
        Assert.Null(DocumentExtractionParser.Parse("{ bozuk json"));
        Assert.Null(DocumentExtractionParser.Parse(""));
    }

    [Fact]
    public void ItemsNestedInsideFields_AreStillFound()
    {
        // Ölçümde görülen yanıt (liste_008): model kalem dizisini `alanlar`ın İÇİNE koydu.
        // Okuduğu 18 hücrenin 18'i doğruydu; kalemleri yalnız kökte aramak bu yanıtı
        // sıfırlıyordu — sessiz veri kaybı.
        var belge = DocumentExtractionParser.Parse("""
            {
              "alanlar": {
                "baslik": null,
                "kalemler": [
                  { "kisi": "Mehmet Yılmaz", "adet": 14, "tutar": 4127.74 },
                  { "kisi": "Elif", "adet": 9, "tutar": 1627.87 }
                ]
              }
            }
            """)!;

        Assert.Equal(2, belge.Items.Count);
        Assert.Equal("4127.74", belge.Items[0]["tutar"]);
        // Kalem dizisi bir "alan" olarak da yazılmamalı.
        Assert.DoesNotContain("kalemler", belge.Fields.Keys);
    }

    // ------------------------------ sayı normalleştirme ------------------------------

    [Theory]
    [InlineData("721,83", "721.83")]        // Türkçe ondalık
    [InlineData("1993.32", "1993.32")]      // aynı yanıtta nokta ondalık da geldi
    [InlineData("14.849,57", "14849.57")]   // Türkçe binlik + ondalık
    [InlineData("14,849.57", "14849.57")]   // İngilizce binlik + ondalık
    [InlineData("1.500", "1500")]           // ayraçtan sonra tam üç hane -> binlik
    [InlineData("1.5", "1.5")]              // üç hane değil -> ondalık
    [InlineData("12.345.678", "12345678")]
    [InlineData("%20", "20")]
    [InlineData("1.500,75 TL", "1500.75")]
    [InlineData("*45,00", "45.00")]         // fiş satırlarında yıldızla geliyor
    [InlineData("20", "20")]
    public void MixedNumberFormats_AreNormalized(string ham, string beklenen) =>
        Assert.Equal(beklenen, DocumentExtractionParser.Normalize(ham));

    [Theory]
    [InlineData("A329258")]                        // fatura no: sayı değil
    [InlineData("Çağdaş İnşaat Taahhüt Ltd. Şti.")]
    [InlineData("Havale/EFT")]
    [InlineData("3585650756")]                     // vergi no: dokunulmuyor, olduğu gibi
    public void NonNumericText_IsLeftAlone(string ham) =>
        Assert.Equal(ham, DocumentExtractionParser.Normalize(ham));

    [Theory]
    [InlineData("2026-01-01")]
    [InlineData("01.01.2026")]
    [InlineData("04/03/2026")]
    public void Dates_AreLeftAlone(string ham) =>
        // Belirsiz tarihi burada çevirmek gün-ay sırasını rastlantıya bırakır. Dokunulmazsa
        // en kötü durumda kolon "text" algılanır: yanlış tarih sessizdir, metin kolon görünür.
        Assert.Equal(ham, DocumentExtractionParser.Normalize(ham));

    // Bu testin varlık sebebi somut bir sessiz bozulma: model bir kolonda "721,83" ve
    // "1993.32" döndürdüğünde, ham değerler ValueFormats'a olduğu gibi verilirse virgülü
    // gören algılayıcı kolonu Türkçe sayar ve "1993.32" 199332 olarak ayrıştırılır.
    // Yüz kat sapma, hata mesajı yok.
    [Fact]
    public void MixedFormatColumn_DoesNotSilentlyInflateValues()
    {
        var belge = DocumentExtractionParser.Parse(DuzYanit)!;
        var tablo = DocumentExtractionParser.ToParsedTable(belge);

        var iceAktarma = new DatasetImportService();
        var sema = iceAktarma.DetectSchema(tablo);
        var sonuc = iceAktarma.ValidateRows(tablo, sema);

        Assert.Equal("number", sema.Single(k => k.Name == "tutar").Type);
        Assert.Empty(sonuc.Errors);
        Assert.Equal(721.83m, Assert.IsType<decimal>(sonuc.ValidRows[0]["tutar"]));
        Assert.Equal(1993.32m, Assert.IsType<decimal>(sonuc.ValidRows[1]["tutar"]));
    }

    // ------------------------------ bilgi taşımayan satırın budanması ------------------------------

    [Fact]
    public void SingleItem_RestatingDocumentFields_IsDropped()
    {
        var belge = DocumentExtractionParser.Parse(UydurmaKalemliYanit)!;

        Assert.Empty(belge.Items);
        Assert.Contains(belge.Notes, n => n.Contains("düşürüldü"));
    }

    [Fact]
    public void SingleItem_WithOwnInformation_IsKept()
    {
        // Tek satırlık gerçek fatura: ürün adı da tutar da belge alanlarında yok.
        var belge = DocumentExtractionParser.Parse("""
            {
              "alanlar": { "satici": "Üçler Kırtasiye", "genel_toplam": "1.200,00" },
              "kalemler": [ { "urun": "A4 fotokopi kâğıdı", "adet": "10", "tutar": "1.000,00" } ]
            }
            """)!;

        Assert.Single(belge.Items);
        Assert.Empty(belge.Notes);
    }

    [Fact]
    public void MultipleItems_AreNeverDropped()
    {
        // Budama yalnız TEK satıra bakıyor: iki satır varsa bağın kendisi bilgidir, ve
        // "acaba gerçek mi" diye tahmin yürütmek düzeltmeye çalıştığımız hatanın aynısını
        // ters yönde yapmak olurdu.
        var belge = DocumentExtractionParser.Parse(DuzYanit)!;

        Assert.Equal(2, belge.Items.Count);
    }

    // ------------------------------ toplam uyuşması ------------------------------

    [Fact]
    public void ItemTotals_MatchingDocumentTotal_ReportsNothing()
    {
        var belge = DocumentExtractionParser.Parse("""
            {
              "alanlar": { "ara_toplam": "2.715,15" },
              "kalemler": [ { "tutar": "721,83" }, { "tutar": "1993.32" } ]
            }
            """)!;

        Assert.Null(DocumentExtractionParser.ToplamUyusmazligi(belge, "tutar", "ara_toplam"));
    }

    [Fact]
    public void ItemTotals_MissingARow_IsReported()
    {
        // Satır atlamanın belirtisi bu: kalemler toplamı belge toplamından küçük kalır.
        var belge = DocumentExtractionParser.Parse("""
            {
              "alanlar": { "ara_toplam": "2.715,15" },
              "kalemler": [ { "tutar": "721,83" } ]
            }
            """)!;

        var uyari = DocumentExtractionParser.ToplamUyusmazligi(belge, "tutar", "ara_toplam");

        Assert.NotNull(uyari);
        Assert.Contains("2715.15", uyari);
    }

    [Fact]
    public void ItemTotals_WithoutAnyDocumentTotal_ReportsNothing() =>
        // Karşılaştırılacak toplam yoksa sessiz kalınır; uyarı üretmek kullanıcıyı
        // olmayan bir sorunla meşgul etmek olur.
        Assert.Null(DocumentExtractionParser.ToplamUyusmazligi(
            DocumentExtractionParser.Parse("""
                { "alanlar": { "satici": "X" }, "kalemler": [ { "tutar": "10,00" } ] }
                """)!,
            "tutar", "ara_toplam"));

    // ------------------------------ var olan içe aktarma yoluna bağlanma ------------------------------

    [Fact]
    public void DocumentFields_AreRepeatedOnEveryItemRow()
    {
        // CSV'den gelen veriyle aynı şekil: pano ve doğal dilde sorgu değişiklik istemiyor.
        var tablo = DocumentExtractionParser.ToParsedTable(DocumentExtractionParser.Parse(DuzYanit)!);

        var saticiIndex = tablo.Headers.ToList().IndexOf("satici");
        Assert.Equal(2, tablo.Rows.Count);
        Assert.All(tablo.Rows, satir =>
            Assert.Equal("Çağdaş İnşaat Taahhüt Ltd. Şti.", satir[saticiIndex]));
    }

    [Fact]
    public void ItemColumn_ShadowsDocumentColumnOfSameName()
    {
        // Aynı ad iki düzeyde olabilir; satırın kendi değeri daha özeldir, o kazanır.
        var tablo = DocumentExtractionParser.ToParsedTable(DocumentExtractionParser.Parse("""
            {
              "alanlar": { "satici": "X", "tutar": "999,00" },
              "kalemler": [ { "urun": "Kablo", "tutar": "120,00" } ]
            }
            """)!);

        Assert.Single(tablo.Headers.Where(b => b == "tutar"));
        Assert.Equal("120.00", tablo.Rows[0][tablo.Headers.ToList().IndexOf("tutar")]);
    }

    [Fact]
    public void ItemlessDocument_BecomesSingleRow()
    {
        var tablo = DocumentExtractionParser.ToParsedTable(
            DocumentExtractionParser.Parse(UydurmaKalemliYanit)!);

        Assert.Single(tablo.Rows);
        Assert.Contains("aciklama", tablo.Headers);
    }
}
