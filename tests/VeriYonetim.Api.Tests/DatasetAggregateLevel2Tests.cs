using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Doğal dil sorguları için genişletilen agregasyon yetenekleri: çoklu gruplama,
// çoklu ölçüm, HAVING ve COUNT(DISTINCT). Hepsi yine whitelist'li fiiller — plan dili
// büyürken güvenlik özelliği (modelin ürettiği hiçbir karakter SQL'e girmez) korunuyor.
public class DatasetAggregateLevel2Tests
{
    private static readonly Dictionary<string, string> Schema = new()
    {
        ["sehir"] = "text",
        ["kategori"] = "text",
        ["tutar"] = "number",
        ["tarih"] = "date"
    };

    private static AggregateQuery Q(
        string[]? groupBy = null,
        MetricSpec[]? metrics = null,
        string? bucket = null,
        string? sort = null,
        string? dir = null,
        int? limit = null,
        HavingSpec? having = null,
        int sortMetric = 0) =>
        new(groupBy ?? Array.Empty<string>(),
            metrics ?? new[] { new MetricSpec("count") },
            bucket, sort, dir, limit, Array.Empty<RowFilter>(), having, sortMetric);

    // --- çoklu gruplama ---------------------------------------------------------------

    [Fact]
    public void MultipleGroupBy_EmitsBothKeysAndGroupsByBoth()
    {
        // "Şehir ve kategoriye göre toplam satış"
        var built = DatasetAggregateQueryBuilder.Build(
            Q(groupBy: new[] { "sehir", "kategori" },
              metrics: new[] { new MetricSpec("sum", "tutar") }), Schema);

        Assert.Contains("AS \"Key0\"", built.Sql);
        Assert.Contains("AS \"Key1\"", built.Sql);
        Assert.Contains("GROUP BY (\"Data\"->>'sehir'), (\"Data\"->>'kategori')", built.Sql);
        Assert.Equal(2, built.KeyCount);
    }

    [Fact]
    public void MultipleGroupBy_BucketAppliesToFirstColumnOnly()
    {
        // "Aylara ve şehre göre toplam" — kovalanacak olan tarih kolonudur, şehir değil.
        var built = DatasetAggregateQueryBuilder.Build(
            Q(groupBy: new[] { "tarih", "sehir" },
              metrics: new[] { new MetricSpec("sum", "tutar") },
              bucket: "month"), Schema);

        // Tarih kovalanır, şehir kovalanmaz. (İfade SELECT ve GROUP BY'da tekrar ettiği
        // için date_trunc birden çok kez geçer; önemli olan hangi kolonu sardığı.)
        Assert.Contains("date_trunc('month', ((\"Data\"->>'tarih'))::timestamp)", built.Sql);
        Assert.DoesNotContain("date_trunc('month', (\"Data\"->>'sehir')", built.Sql);
    }

    [Fact]
    public void MultipleGroupBy_ExceedingLimit_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(groupBy: new[] { "sehir", "kategori", "tutar", "tarih" }), Schema));
    }

    [Fact]
    public void DuplicateGroupBy_Throws()
    {
        // Aynı kolonla iki kez gruplamak sessizce anlamsız bir sonuç üretirdi.
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(groupBy: new[] { "sehir", "sehir" }), Schema));
    }

    // --- çoklu ölçüm ------------------------------------------------------------------

    [Fact]
    public void MultipleMetrics_EmitAllValueColumns()
    {
        // "Şehirlere göre toplam, ortalama ve adet — hepsi bir arada"
        var built = DatasetAggregateQueryBuilder.Build(
            Q(groupBy: new[] { "sehir" },
              metrics: new[]
              {
                  new MetricSpec("sum", "tutar"),
                  new MetricSpec("avg", "tutar"),
                  new MetricSpec("count")
              }), Schema);

        Assert.Contains("SUM(", built.Sql);
        Assert.Contains("AVG(", built.Sql);
        Assert.Contains("AS \"Value0\"", built.Sql);
        Assert.Contains("AS \"Value1\"", built.Sql);
        Assert.Contains("AS \"Value2\"", built.Sql);
        Assert.Equal(3, built.MetricCount);
    }

    [Fact]
    public void NoMetrics_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(groupBy: new[] { "sehir" }, metrics: Array.Empty<MetricSpec>()), Schema));
    }

    [Fact]
    public void TooManyMetrics_Throws()
    {
        var many = Enumerable.Repeat(new MetricSpec("count"), 7).ToArray();
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(Q(groupBy: new[] { "sehir" }, metrics: many), Schema));
    }

    [Fact]
    public void SortMetric_SelectsWhichValueColumnOrdersResult()
    {
        // Çoklu ölçümde "değere göre sırala" tek başına belirsiz; indeks açıkça verilir.
        var built = DatasetAggregateQueryBuilder.Build(
            Q(groupBy: new[] { "sehir" },
              metrics: new[] { new MetricSpec("count"), new MetricSpec("sum", "tutar") },
              sort: "value", dir: "desc", sortMetric: 1), Schema);

        Assert.Contains("ORDER BY \"Value1\" DESC", built.Sql);
    }

    [Fact]
    public void SortMetric_OutOfRange_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(groupBy: new[] { "sehir" }, sort: "value", sortMetric: 5), Schema));
    }

    // --- COUNT(DISTINCT) --------------------------------------------------------------

    [Fact]
    public void CountDistinct_WorksOnTextColumn()
    {
        // "Kaç farklı şehre satış yaptık?" — COUNT(*)'tan bambaşka bir soru, ve metin
        // kolonu üzerinde sorulur (sum/avg'ın sayısal kısıtı buraya uygulanmamalı).
        var built = DatasetAggregateQueryBuilder.Build(
            Q(metrics: new[] { new MetricSpec("countDistinct", "sehir") }), Schema);

        Assert.Contains("COUNT(DISTINCT (\"Data\"->>'sehir'))", built.Sql);
    }

    [Fact]
    public void CountDistinct_WithoutColumn_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(metrics: new[] { new MetricSpec("countDistinct") }), Schema));
    }

    [Fact]
    public void CountDistinct_UnknownColumn_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(metrics: new[] { new MetricSpec("countDistinct", "yok") }), Schema));
    }

    // --- HAVING -----------------------------------------------------------------------

    [Fact]
    public void Having_EmitsHavingClauseWithParameter()
    {
        // "Ortalama satışı 1000'in üstünde olan şehirler" — WHERE ile ifade EDİLEMEZ,
        // çünkü koşul satırın değil grubun ortalamasıyla ilgilidir.
        var built = DatasetAggregateQueryBuilder.Build(
            Q(groupBy: new[] { "sehir" },
              metrics: new[] { new MetricSpec("avg", "tutar") },
              having: new HavingSpec(0, "gt", 1000m)), Schema);

        Assert.Contains("HAVING AVG(", built.Sql);
        Assert.Contains("> @h0", built.Sql);
        Assert.Equal(1000m, Assert.Single(built.Parameters).Value);
    }

    [Fact]
    public void Having_RepeatsExpression_NotSelectAlias()
    {
        // PostgreSQL HAVING içinde SELECT takma adlarını KABUL ETMEZ (ORDER BY'ın aksine).
        // Alias yazılsaydı sorgu çalışma zamanında patlardı.
        var built = DatasetAggregateQueryBuilder.Build(
            Q(groupBy: new[] { "sehir" },
              metrics: new[] { new MetricSpec("avg", "tutar") },
              having: new HavingSpec(0, "gt", 1000m)), Schema);

        Assert.DoesNotContain("HAVING \"Value0\"", built.Sql);
    }

    [Fact]
    public void Having_TargetsSelectedMetricByIndex()
    {
        var built = DatasetAggregateQueryBuilder.Build(
            Q(groupBy: new[] { "sehir" },
              metrics: new[] { new MetricSpec("count"), new MetricSpec("sum", "tutar") },
              having: new HavingSpec(1, "gte", 500m)), Schema);

        Assert.Contains("HAVING SUM(", built.Sql);
    }

    [Fact]
    public void Having_WithoutGroupBy_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(having: new HavingSpec(0, "gt", 1m)), Schema));
    }

    [Fact]
    public void Having_MetricIndexOutOfRange_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(groupBy: new[] { "sehir" }, having: new HavingSpec(3, "gt", 1m)), Schema));
    }

    [Fact]
    public void Having_UnknownOperator_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(groupBy: new[] { "sehir" }, having: new HavingSpec(0, "hack", 1m)), Schema));
    }

    // --- geriye uyumluluk -------------------------------------------------------------

    [Fact]
    public void LegacyConstructor_MapsToSingleGroupAndSingleMetric()
    {
        // Querystring ucu (/aggregate) eski biçimi kullanmaya devam ediyor; kırılmamalı.
        var q = new AggregateQuery("sehir", "sum", "tutar", null, null, null, null,
            Array.Empty<RowFilter>());

        Assert.Equal(new[] { "sehir" }, q.GroupBy);
        var metric = Assert.Single(q.Metrics);
        Assert.Equal("sum", metric.Op);
        Assert.Equal("tutar", metric.Column);
    }

    [Fact]
    public void LegacyConstructor_NullGroupBy_MeansNoGrouping()
    {
        var q = new AggregateQuery(null, "count", null, null, null, null, null,
            Array.Empty<RowFilter>());

        Assert.Empty(q.GroupBy);
    }

    [Fact]
    public void NoGroupBy_StillReportsOneKeyColumn()
    {
        // Gruplamasızda da tek bir NULL anahtar yazılır ki okuyucunun sütun düzeni
        // her iki halde de aynı olsun.
        var built = DatasetAggregateQueryBuilder.Build(Q(), Schema);

        Assert.Equal(1, built.KeyCount);
        Assert.Contains("NULL::text AS \"Key0\"", built.Sql);
    }
}
