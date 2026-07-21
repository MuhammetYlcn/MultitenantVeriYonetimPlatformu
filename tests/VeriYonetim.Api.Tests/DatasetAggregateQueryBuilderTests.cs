using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Saf birim testler: DatasetAggregateQueryBuilder DB/HTTP'siz SQL + parametre üretimini
// doğrular. Odak: doğru agregasyon/GROUP BY, tip cast, whitelist reddi ve injection escape.
public class DatasetAggregateQueryBuilderTests
{
    private static readonly Dictionary<string, string> Schema = new()
    {
        ["sehir"] = "text",
        ["yas"] = "number",
        ["tutar"] = "number",
        ["tarih"] = "date"
    };

    private static AggregateQuery Q(string groupBy, string op, string? metric = null,
        string? bucket = null, string? sort = null, string? dir = null, int? limit = null,
        params RowFilter[] filters) =>
        new(groupBy, op, metric, bucket, sort, dir, limit, filters);

    [Fact]
    public void Build_GroupAvg_EmitsAvgWithNumericCastAndGroupBy()
    {
        var built = DatasetAggregateQueryBuilder.Build(Q("sehir", "avg", "yas"), Schema);

        Assert.Contains("AVG(", built.Sql);
        Assert.Contains("\"Data\"->>'yas'", built.Sql);
        Assert.Contains("::numeric", built.Sql);
        Assert.Contains("GROUP BY (\"Data\"->>'sehir')", built.Sql);
        Assert.Contains("WHERE \"DatasetId\" = @datasetId", built.Sql);
    }

    [Fact]
    public void Build_NoGroupBy_OverallAggregate_HasNoGroupByClause()
    {
        // groupBy verilmezse gruplamasız genel agregasyon: GROUP BY yok, key NULL.
        var built = DatasetAggregateQueryBuilder.Build(
            new AggregateQuery(null, "sum", "tutar", null, null, null, null, Array.Empty<RowFilter>()),
            Schema);

        Assert.DoesNotContain("GROUP BY", built.Sql);
        Assert.Contains("NULL::text AS \"Key\"", built.Sql);
        Assert.Contains("SUM(", built.Sql);
    }

    [Fact]
    public void Build_BucketWithoutGroupBy_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                new AggregateQuery(null, "sum", "tutar", "month", null, null, null, Array.Empty<RowFilter>()),
                Schema));
    }

    [Fact]
    public void Build_Count_NeedsNoMetric()
    {
        var built = DatasetAggregateQueryBuilder.Build(Q("sehir", "count"), Schema);
        Assert.Contains("COUNT(*)::numeric AS \"Value\"", built.Sql);
    }

    [Fact]
    public void Build_TopN_OrdersByValueDescWithLimit()
    {
        var built = DatasetAggregateQueryBuilder.Build(
            Q("sehir", "sum", "tutar", sort: "value", dir: "desc", limit: 5), Schema);

        Assert.Contains("ORDER BY \"Value\" DESC", built.Sql);
        Assert.Contains("LIMIT 5", built.Sql);
    }

    [Fact]
    public void Build_TimeSeries_UsesDateTrunc()
    {
        var built = DatasetAggregateQueryBuilder.Build(
            Q("tarih", "sum", "tutar", bucket: "month"), Schema);

        Assert.Contains("date_trunc('month',", built.Sql);
        Assert.Contains("\"Data\"->>'tarih'", built.Sql);
        Assert.Contains("::timestamp", built.Sql);
    }

    [Fact]
    public void Build_LimitClampedToMax()
    {
        var built = DatasetAggregateQueryBuilder.Build(Q("sehir", "count", limit: 9999), Schema);
        Assert.Contains("LIMIT 1000", built.Sql);
    }

    [Fact]
    public void Build_FilterReused_AddsWhereAndParam()
    {
        var built = DatasetAggregateQueryBuilder.Build(
            Q("sehir", "count", filters: new RowFilter("yas", "gte", "30")), Schema);

        Assert.Contains(">= @f0", built.Sql);
        Assert.Equal(30m, Assert.Single(built.Parameters).Value);
    }

    [Fact]
    public void Build_GroupByColumnWithQuote_IsEscaped()
    {
        var schema = new Dictionary<string, string> { ["a'b"] = "text" };
        var built = DatasetAggregateQueryBuilder.Build(Q("a'b", "count"), schema);
        Assert.Contains("'a''b'", built.Sql);
    }

    [Fact]
    public void Build_BucketOnNonDate_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(Q("sehir", "count", bucket: "month"), Schema));
    }

    [Fact]
    public void Build_UnknownGroupBy_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(Q("yok", "count"), Schema));
    }

    [Fact]
    public void Build_UnknownOp_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(Q("sehir", "hack"), Schema));
    }

    [Fact]
    public void Build_SumWithoutMetric_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(Q("sehir", "sum"), Schema));
    }

    [Fact]
    public void Build_SumOnTextMetric_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(Q("yas", "sum", "sehir"), Schema));
    }

    [Fact]
    public void Build_UnknownBucket_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(Q("tarih", "count", bucket: "century"), Schema));
    }
}
