using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Seviye 3 yetenekleri: VEYA filtre ağacı, pay/yüzde ve dönem karşılaştırma.
public class DatasetAggregateLevel3Tests
{
    private static readonly Dictionary<string, string> Schema = new()
    {
        ["sehir"] = "text",
        ["kategori"] = "text",
        ["tutar"] = "number",
        ["tarih"] = "date"
    };

    private static BuiltQuery Where(params FilterNode[] filters) =>
        DatasetRowQueryBuilder.Build(new RowQuery(1, 25, null, null, filters), Schema);

    // --- VEYA filtre ağacı ------------------------------------------------------------

    [Fact]
    public void OrGroup_JoinsChildrenWithOr()
    {
        // "Ankara'daki VEYA 1000 TL üstü satışlar"
        var built = Where(new FilterGroup("or", new FilterNode[]
        {
            new RowFilter("sehir", "eq", "Ankara"),
            new RowFilter("tutar", "gt", "1000")
        }));

        Assert.Contains(" OR ", built.WhereSql);
        Assert.Equal(2, built.Parameters.Count);
    }

    [Fact]
    public void OrGroup_IsParenthesised()
    {
        // Parantez şart: AND, OR'dan önce bağlar. "a AND (b OR c)" ile "a AND b OR c"
        // farklı sorulardır ve ikincisi sessizce yanlış sonuç verir.
        var built = Where(
            new RowFilter("kategori", "eq", "Elektronik"),
            new FilterGroup("or", new FilterNode[]
            {
                new RowFilter("sehir", "eq", "Ankara"),
                new RowFilter("tutar", "gt", "1000")
            }));

        Assert.Contains("((\"Data\"->>'sehir') = @f1 OR", built.WhereSql);
        Assert.EndsWith(")", built.WhereSql);
    }

    [Fact]
    public void AndGroup_JoinsChildrenWithAnd()
    {
        var built = Where(new FilterGroup("and", new FilterNode[]
        {
            new RowFilter("sehir", "eq", "Ankara"),
            new RowFilter("tutar", "gt", "1000")
        }));

        Assert.Contains(" AND ", built.WhereSql);
        Assert.DoesNotContain(" OR ", built.WhereSql);
    }

    [Fact]
    public void NestedGroups_ParameterNumberingStaysUnique()
    {
        // İç içe ağaçta sayaç tek akış üzerinden ilerlemeli; aksi halde iki farklı koşul
        // aynı @f adını kullanır ve sorgu sessizce yanlış olur.
        var built = Where(new FilterGroup("or", new FilterNode[]
        {
            new RowFilter("sehir", "eq", "Ankara"),
            new FilterGroup("and", new FilterNode[]
            {
                new RowFilter("kategori", "eq", "Elektronik"),
                new RowFilter("tutar", "gte", "500")
            })
        }));

        Assert.Equal(new[] { "f0", "f1", "f2" }, built.Parameters.Select(p => p.ParameterName));
    }

    [Fact]
    public void GroupWithMultiParameterLeaf_KeepsNumberingConsistent()
    {
        // inPeriod tek başına iki parametre üretir; ağacın içinde de öyle davranmalı.
        var built = Where(new FilterGroup("or", new FilterNode[]
        {
            new RowFilter("tarih", "inPeriod", "buAy"),
            new RowFilter("sehir", "eq", "Ankara")
        }));

        Assert.Equal(new[] { "f0", "f1", "f2" }, built.Parameters.Select(p => p.ParameterName));
    }

    [Fact]
    public void TooDeepNesting_Throws()
    {
        FilterNode node = new RowFilter("sehir", "eq", "Ankara");
        for (var i = 0; i < 5; i++)
            node = new FilterGroup("or", new[] { node });

        Assert.Throws<InvalidQueryException>(() => Where(node));
    }

    [Fact]
    public void EmptyGroup_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            Where(new FilterGroup("or", Array.Empty<FilterNode>())));
    }

    [Fact]
    public void UnknownLogic_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            Where(new FilterGroup("xor", new FilterNode[] { new RowFilter("sehir", "eq", "A") })));
    }

    [Fact]
    public void GroupChild_StillValidatedAgainstSchema()
    {
        // Ağaç, whitelist'i atlamanın yolu OLMAMALI.
        Assert.Throws<InvalidQueryException>(() =>
            Where(new FilterGroup("or", new FilterNode[] { new RowFilter("yok", "eq", "1") })));
    }

    // --- pay / yüzde ------------------------------------------------------------------

    private static AggregateQuery Agg(
        string[]? groupBy = null, MetricSpec[]? metrics = null,
        bool share = false, int shareMetric = 0) =>
        new(groupBy ?? Array.Empty<string>(),
            metrics ?? new[] { new MetricSpec("sum", "tutar") },
            null, null, null, null, Array.Empty<FilterNode>(), null, 0, share, shareMetric);

    [Fact]
    public void Share_EmitsWindowFunctionColumn()
    {
        // "Her şehrin toplam ciro içindeki yüzdesi"
        var built = DatasetAggregateQueryBuilder.Build(
            Agg(groupBy: new[] { "sehir" }, share: true), Schema);

        Assert.Contains("OVER ()", built.Sql);
        Assert.Contains("AS \"Share\"", built.Sql);
        Assert.True(built.HasShare);
    }

    [Fact]
    public void Share_GuardsAgainstDivisionByZero()
    {
        var built = DatasetAggregateQueryBuilder.Build(
            Agg(groupBy: new[] { "sehir" }, share: true), Schema);

        Assert.Contains("NULLIF(", built.Sql);
    }

    [Fact]
    public void Share_ComesAfterCountColumn()
    {
        // Okuyucu indeksleri pay'ın varlığına göre kaydırmasın diye Share en sonda olmalı.
        var built = DatasetAggregateQueryBuilder.Build(
            Agg(groupBy: new[] { "sehir" }, share: true), Schema);

        Assert.True(built.Sql.IndexOf("AS \"Count\"", StringComparison.Ordinal)
                  < built.Sql.IndexOf("AS \"Share\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Share_WithoutGroupBy_Throws()
    {
        // Pay, bir grubun bütün içindeki oranıdır; tek satırda anlamı yok.
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(Agg(share: true), Schema));
    }

    [Fact]
    public void Share_OnAverage_Throws()
    {
        // Ortalamaların toplamı bir bütün etmez; "ortalamanın yüzdesi" anlamsızdır.
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Agg(groupBy: new[] { "sehir" },
                    metrics: new[] { new MetricSpec("avg", "tutar") }, share: true), Schema));
    }

    [Fact]
    public void Share_OnCount_IsAllowed()
    {
        var built = DatasetAggregateQueryBuilder.Build(
            Agg(groupBy: new[] { "sehir" }, metrics: new[] { new MetricSpec("count") },
                share: true), Schema);

        Assert.Contains("AS \"Share\"", built.Sql);
    }

    [Fact]
    public void Share_NotRequested_EmitsNoShareColumn()
    {
        var built = DatasetAggregateQueryBuilder.Build(Agg(groupBy: new[] { "sehir" }), Schema);

        Assert.DoesNotContain("Share", built.Sql);
        Assert.False(built.HasShare);
    }

    // --- dönem karşılaştırma ----------------------------------------------------------

    [Fact]
    public void Compare_ComputesDeltaAndPercent()
    {
        var result = PeriodComparison.Merge(
            current: new (string?, decimal?)[] { ("Ankara", 150m) },
            previous: new (string?, decimal?)[] { ("Ankara", 100m) });

        var b = Assert.Single(result);
        Assert.Equal(150m, b.Current);
        Assert.Equal(100m, b.Previous);
        Assert.Equal(50m, b.Delta);
        Assert.Equal(50m, b.DeltaPercent);
    }

    [Fact]
    public void Compare_NegativeChange_IsReported()
    {
        var result = PeriodComparison.Merge(
            current: new (string?, decimal?)[] { ("Ankara", 80m) },
            previous: new (string?, decimal?)[] { ("Ankara", 100m) });

        Assert.Equal(-20m, Assert.Single(result).Delta);
        Assert.Equal(-20m, result[0].DeltaPercent);
    }

    [Fact]
    public void Compare_MissingPrevious_LeavesDeltaNull_NotZero()
    {
        // Eksik dönem 0 sayılsaydı "sonsuz artış" ya da uydurma bir yüzde üretirdik.
        var result = PeriodComparison.Merge(
            current: new (string?, decimal?)[] { ("Yeni", 100m) },
            previous: Array.Empty<(string?, decimal?)>());

        var b = Assert.Single(result);
        Assert.Null(b.Previous);
        Assert.Null(b.Delta);
        Assert.Null(b.DeltaPercent);
    }

    [Fact]
    public void Compare_GroupOnlyInPrevious_StillAppears()
    {
        // "Geçen ay vardı, bu ay hiç yok" bilgisi kaybolmamalı.
        var result = PeriodComparison.Merge(
            current: new (string?, decimal?)[] { ("Ankara", 100m) },
            previous: new (string?, decimal?)[] { ("Ankara", 90m), ("Bursa", 40m) });

        var bursa = result.Single(b => b.Key == "Bursa");
        Assert.Null(bursa.Current);
        Assert.Equal(40m, bursa.Previous);
    }

    [Fact]
    public void Compare_PreviousZero_LeavesPercentNull()
    {
        // Sıfıra bölme yerine "yüzde hesaplanamaz".
        var result = PeriodComparison.Merge(
            current: new (string?, decimal?)[] { ("Ankara", 100m) },
            previous: new (string?, decimal?)[] { ("Ankara", 0m) });

        var b = Assert.Single(result);
        Assert.Equal(100m, b.Delta);
        Assert.Null(b.DeltaPercent);
    }

    [Fact]
    public void Compare_NegativeBase_KeepsSignOfChange()
    {
        // -100'den -50'ye çıkmak bir ARTIŞTIR; mutlak değerle bölünmezse işaret ters dönerdi.
        var result = PeriodComparison.Merge(
            current: new (string?, decimal?)[] { ("Ankara", -50m) },
            previous: new (string?, decimal?)[] { ("Ankara", -100m) });

        Assert.Equal(50m, Assert.Single(result).Delta);
        Assert.Equal(50m, result[0].DeltaPercent);
    }

    [Fact]
    public void Compare_PreservesCurrentOrder()
    {
        var result = PeriodComparison.Merge(
            current: new (string?, decimal?)[] { ("B", 2m), ("A", 1m) },
            previous: new (string?, decimal?)[] { ("A", 1m), ("B", 1m) });

        Assert.Equal(new[] { "B", "A" }, result.Select(r => r.Key));
    }
}
