using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Saf birim testler: DatasetRowQueryBuilder DB/HTTP'siz SQL + parametre üretimini doğrular.
// Odak: tip-farkında cast, injection'a kapalılık ve geçersiz girdilerin reddi.
public class DatasetRowQueryBuilderTests
{
    private static readonly Dictionary<string, string> Schema = new()
    {
        ["ad"] = "text",
        ["yas"] = "number",
        ["tarih"] = "date"
    };

    private static RowQuery Query(string? sort = null, string? dir = null, params RowFilter[] filters) =>
        new(1, 25, sort, dir, filters);

    [Fact]
    public void Build_NoFiltersNoSort_ProducesEmptyClauses()
    {
        var built = DatasetRowQueryBuilder.Build(Query(), Schema);

        Assert.Equal("", built.WhereSql);
        Assert.Equal("", built.OrderBySql);
        Assert.Empty(built.Parameters);
    }

    [Fact]
    public void Build_NumberFilter_CastsToNumericAndParameterizesValue()
    {
        var built = DatasetRowQueryBuilder.Build(
            Query(filters: new RowFilter("yas", "gte", "30")), Schema);

        Assert.Contains("::numeric", built.WhereSql);
        Assert.Contains(">= @f0", built.WhereSql);
        var p = Assert.Single(built.Parameters);
        Assert.Equal(30m, p.Value);                       // string "30" → decimal parametre
    }

    [Fact]
    public void Build_DateFilter_CastsToTimestamp()
    {
        var built = DatasetRowQueryBuilder.Build(
            Query(filters: new RowFilter("tarih", "lt", "2026-01-01")), Schema);

        Assert.Contains("::timestamp", built.WhereSql);
        Assert.Equal(new DateTime(2026, 1, 1), Assert.Single(built.Parameters).Value);
    }

    [Fact]
    public void Build_ContainsOnText_UsesIlikeWithWildcards()
    {
        var built = DatasetRowQueryBuilder.Build(
            Query(filters: new RowFilter("ad", "contains", "Al")), Schema);

        Assert.Contains("ILIKE @f0", built.WhereSql);
        Assert.Equal("%Al%", Assert.Single(built.Parameters).Value);
    }

    [Fact]
    public void Build_SortDesc_EmitsTypedOrderByDesc()
    {
        var built = DatasetRowQueryBuilder.Build(Query(sort: "yas", dir: "desc"), Schema);

        Assert.Contains("ORDER BY", built.OrderBySql);
        Assert.Contains("::numeric", built.OrderBySql);   // sayısal sıralama (metinsel değil)
        Assert.Contains("DESC", built.OrderBySql);
    }

    [Fact]
    public void Build_MultipleFilters_AllAndedWithDistinctParams()
    {
        var built = DatasetRowQueryBuilder.Build(
            Query(filters: new[]
            {
                new RowFilter("yas", "gte", "18"),
                new RowFilter("ad", "contains", "a")
            }), Schema);

        Assert.Contains("@f0", built.WhereSql);
        Assert.Contains("@f1", built.WhereSql);
        Assert.Equal(2, built.Parameters.Count);
    }

    [Fact]
    public void Build_ColumnNameWithQuote_IsEscaped_NotInjectable()
    {
        // Kolon adı SQL'e gömülür; tek tırnak ikiye katlanmalı (injection'a kapalı).
        var schema = new Dictionary<string, string> { ["a'b"] = "text" };
        var built = DatasetRowQueryBuilder.Build(
            Query(filters: new RowFilter("a'b", "eq", "x")), schema);

        Assert.Contains("'a''b'", built.WhereSql);        // 'a''b' — güvenli literal
    }

    [Fact]
    public void Build_UnknownColumn_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetRowQueryBuilder.Build(Query(filters: new RowFilter("yok", "eq", "1")), Schema));
    }

    [Fact]
    public void Build_UnknownOperator_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetRowQueryBuilder.Build(Query(filters: new RowFilter("yas", "hack", "1")), Schema));
    }

    [Fact]
    public void Build_ContainsOnNumber_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetRowQueryBuilder.Build(Query(filters: new RowFilter("yas", "contains", "3")), Schema));
    }

    [Fact]
    public void Build_UnknownSortColumn_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetRowQueryBuilder.Build(Query(sort: "yok"), Schema));
    }

    [Fact]
    public void Build_BadNumberValue_Throws()
    {
        Assert.Throws<InvalidQueryException>(() =>
            DatasetRowQueryBuilder.Build(Query(filters: new RowFilter("yas", "gte", "abc")), Schema));
    }
}
