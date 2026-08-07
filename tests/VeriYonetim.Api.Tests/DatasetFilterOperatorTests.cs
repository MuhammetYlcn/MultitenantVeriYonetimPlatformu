using Npgsql;
using NpgsqlTypes;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Doğal dil sorguları için eklenen filtre operatörlerini (in/notIn, isNull/notNull,
// inPeriod) doğrular. Ortak WHERE üreticisi üzerinden test edildiğinden hem satır
// listeleme hem agregasyon bu davranışı devralır.
public class DatasetFilterOperatorTests
{
    private static readonly Dictionary<string, string> Schema = new()
    {
        ["sehir"] = "text",
        ["tutar"] = "number",
        ["tarih"] = "date"
    };

    // WHERE ekini ve parametreleri üretir (sıralama/sayfalama bu testlerin konusu değil).
    private static BuiltQuery Where(params RowFilter[] filters) =>
        DatasetRowQueryBuilder.Build(new RowQuery(1, 25, null, null, filters), Schema);

    private static RowFilter In(string column, string op, params string[] values) =>
        new(column, op, "", values);

    // --- in / notIn -------------------------------------------------------------------

    [Fact]
    public void In_UsesAnyWithSingleArrayParameter()
    {
        // "Ankara ve İzmir'deki satışlar" — iki eq filtresi VE ile bağlanıp hiçbir satır
        // döndürmezdi; tek koşulda çoklu değer bu yüzden gerekli.
        var built = Where(In("sehir", "in", "Ankara", "İzmir"));

        // Metin kolonunda karşılaştırma harfe duyarsız: dizi de unnest ile lower'dan geçer.
        Assert.Contains("= ANY(SELECT lower(v) FROM unnest(@f0) AS v)", built.WhereSql);
        var p = Assert.Single(built.Parameters);
        Assert.Equal(new[] { "Ankara", "İzmir" }, p.Value);
    }

    [Fact]
    public void NotIn_UsesAllWithInequality()
    {
        var built = Where(In("sehir", "notIn", "Ankara"));
        Assert.Contains("<> ALL(SELECT lower(v) FROM unnest(@f0) AS v)", built.WhereSql);
    }

    [Fact]
    public void In_OnNumberColumn_ParsesValuesToDecimalArray()
    {
        // Değerler metin gelir; dizi de tipli parametre olmalı ki "100" < "9" gibi
        // metinsel kıyas oluşmasın.
        var built = Where(In("tutar", "in", "100", "250.5"));

        var p = Assert.Single(built.Parameters);
        Assert.Equal(new[] { 100m, 250.5m }, p.Value);
        Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Numeric, p.NpgsqlDbType);
    }

    [Fact]
    public void In_ValuesAreParameters_NotInlinedIntoSql()
    {
        // Injection yüzeyi kontrolü: değer SQL metnine gömülmemeli.
        var built = Where(In("sehir", "in", "x'); DROP TABLE \"DatasetRows\"; --"));

        Assert.DoesNotContain("DROP", built.WhereSql);
        Assert.Contains("unnest(@f0)", built.WhereSql);
    }

    [Fact]
    public void In_WithNoValues_Throws()
    {
        Assert.Throws<InvalidQueryException>(() => Where(In("sehir", "in")));
    }

    [Fact]
    public void In_ExceedingMaxValues_Throws()
    {
        var many = Enumerable.Range(0, 201).Select(i => i.ToString()).ToArray();
        Assert.Throws<InvalidQueryException>(() => Where(In("tutar", "in", many)));
    }

    [Fact]
    public void In_WithBadNumberValue_Throws()
    {
        Assert.Throws<InvalidQueryException>(() => Where(In("tutar", "in", "100", "abc")));
    }

    // --- isNull / notNull -------------------------------------------------------------

    [Fact]
    public void IsNull_EmitsIsNullAndConsumesNoParameter()
    {
        // Boş hücre içeri alınırken JSON null yazıldığından ->> de NULL döner.
        var built = Where(new RowFilter("tutar", "isNull"));

        Assert.Contains("IS NULL", built.WhereSql);
        Assert.Empty(built.Parameters);
    }

    [Fact]
    public void NotNull_EmitsIsNotNull()
    {
        var built = Where(new RowFilter("tutar", "notNull"));
        Assert.Contains("IS NOT NULL", built.WhereSql);
    }

    [Fact]
    public void IsNull_UsesRawTextExpression_WithoutCast()
    {
        // Cast edilseydi ("Data"->>'tutar')::numeric — NULL zaten cast edilemez, gereksiz.
        var built = Where(new RowFilter("tutar", "isNull"));
        Assert.DoesNotContain("::numeric", built.WhereSql);
    }

    // --- inPeriod ---------------------------------------------------------------------

    [Fact]
    public void InPeriod_EmitsHalfOpenRangeWithTwoParameters()
    {
        var built = Where(new RowFilter("tarih", "inPeriod", "gecenAy"));

        Assert.Contains(">= @f0", built.WhereSql);
        Assert.Contains("< @f1", built.WhereSql);      // "<=" DEĞİL: bitiş dışlayıcı
        Assert.Equal(2, built.Parameters.Count);
    }

    [Fact]
    public void InPeriod_ParametersAreTimestamps()
    {
        var built = Where(new RowFilter("tarih", "inPeriod", "buYil"));

        foreach (var p in built.Parameters)
        {
            Assert.Equal(NpgsqlDbType.Timestamp, p.NpgsqlDbType);
            Assert.Equal(DateTimeKind.Unspecified, ((DateTime)p.Value!).Kind);
        }
    }

    [Fact]
    public void InPeriod_OnNonDateColumn_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            Where(new RowFilter("sehir", "inPeriod", "buAy")));
    }

    [Fact]
    public void InPeriod_UnknownToken_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            Where(new RowFilter("tarih", "inPeriod", "gelecekAy")));
    }

    // --- parametre numaralandırma -----------------------------------------------------

    [Fact]
    public void MultiParameterFilter_DoesNotCollideWithFollowingFilters()
    {
        // inPeriod İKİ parametre üretir (@f0, @f1). Sonraki filtre @f2'den devam etmeli;
        // sayaç tek tek artsaydı @f1 iki kez bağlanır ve sorgu sessizce yanlış olurdu.
        var built = Where(
            new RowFilter("tarih", "inPeriod", "buAy"),
            new RowFilter("tutar", "gte", "100"));

        Assert.Contains("@f2", built.WhereSql);
        Assert.Equal(3, built.Parameters.Count);
        Assert.Equal(new[] { "f0", "f1", "f2" }, built.Parameters.Select(p => p.ParameterName));
    }

    [Fact]
    public void IsNull_DoesNotAdvanceParameterCounter()
    {
        // Parametresiz operatör sayacı ilerletmemeli; ilerletseydi @f0 hiç bağlanmayan
        // bir isim olur, sonraki filtre @f1'e giderdi ve Npgsql eksik parametreden patlardı.
        var built = Where(
            new RowFilter("tutar", "isNull"),
            new RowFilter("sehir", "eq", "Ankara"));

        Assert.Contains("@f0", built.WhereSql);
        Assert.Equal("f0", Assert.Single(built.Parameters).ParameterName);
    }
}
