using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Metin karşılaştırmasında harf büyüklüğü.
//
// Neden önemli? Kullanıcı "durumu gecikmiş olan faturalar" diye sorar, veride "Gecikmiş"
// yazar. Düz '=' bunu eşleştirmez: sorgu SIFIR satır döner ve hata da vermez — kullanıcı
// "hiç gecikmiş fatura yokmuş" sanır. Sistemin en tehlikeli hata türü olan sessiz yanlış
// cevabın ta kendisi. contains zaten ILIKE ile harfe duyarsızdı; eq/ne/in/notIn geride
// kalmıştı.
//
// Küçültme SQL'de yapılıyor (C#'ta değil): kural veritabanının derlemesine ait, iki dilde
// ayrı ayrı uygulanırsa Türkçe I/İ gibi harflerde ayrışabilirler.
public class TextComparisonCaseTests
{
    private static readonly Dictionary<string, string> Schema = new()
    {
        ["durum"] = "text",
        ["tutar"] = "number",
        ["tarih"] = "date"
    };

    private static string Where(params RowFilter[] filters) =>
        DatasetRowQueryBuilder.Build(new RowQuery(1, 25, null, null, filters), Schema).WhereSql;

    [Theory]
    [InlineData("eq")]
    [InlineData("ne")]
    public void TextEquality_ComparesBothSidesLowered(string op)
    {
        var sql = Where(new RowFilter("durum", op, "gecikmiş"));

        Assert.Contains("lower((\"Data\"->>'durum'))", sql);
        Assert.Contains("lower(@f0)", sql);
    }

    [Fact]
    public void TextIn_LowersTheArrayToo()
    {
        // Dizinin kendisi küçültülemez; unnest ile açılıp her değer lower'dan geçiriliyor.
        var sql = Where(new RowFilter("durum", "in", "", new[] { "Gecikmiş", "ÖDENDİ" }));

        Assert.Contains("lower((\"Data\"->>'durum')) = ANY(SELECT lower(v) FROM unnest(@f0) AS v)", sql);
    }

    [Fact]
    public void TextNotIn_LowersTheArrayToo()
    {
        var sql = Where(new RowFilter("durum", "notIn", "", new[] { "Gecikmiş" }));

        Assert.Contains("<> ALL(SELECT lower(v) FROM unnest(@f0) AS v)", sql);
    }

    [Theory]
    [InlineData("tutar", "eq", "100")]
    [InlineData("tutar", "gt", "100")]
    [InlineData("tarih", "gte", "2026-01-01")]
    public void NonTextColumns_AreNotLowered(string column, string op, string value)
    {
        // Sayı ve tarihte harf büyüklüğü diye bir şey yok; lower() eklemek hem anlamsız
        // hem de cast'i bozardı (sayısal kıyas metinsel kıyasa düşerdi).
        var sql = Where(new RowFilter(column, op, value));

        Assert.DoesNotContain("lower(", sql);
    }

    [Fact]
    public void TextOrderingComparison_StaysCaseSensitive()
    {
        // gt/lt metinde de kullanılabilir (alfabetik sıra). Orada küçültmek sıralamayı
        // değiştirirdi; kural yalnızca EŞİTLİK karşılaştırmaları için.
        var sql = Where(new RowFilter("durum", "gt", "M"));

        Assert.DoesNotContain("lower(", sql);
    }
}
