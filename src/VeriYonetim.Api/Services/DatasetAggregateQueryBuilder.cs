using Npgsql;

namespace VeriYonetim.Api.Services;

// Agregasyon isteğinin parçaları. groupBy + op zorunlu; metric sum/avg/min/max için gerekli.
// bucket yalnızca tarih kolonlarında (date_trunc ile zaman serisi). sort: "key"|"value".
public record AggregateQuery(
    string GroupBy,
    string Op,
    string? Metric,
    string? Bucket,
    string? Sort,
    string? Dir,
    int? Limit,
    IReadOnlyList<RowFilter> Filters);

// Build çıktısı: tam SQL (@datasetId + @f0.. yer tutucularıyla) ve filtre parametreleri.
public record BuiltAggregate(string Sql, IReadOnlyList<NpgsqlParameter> Parameters);

// JSONB üzerinde GROUP BY + agregasyon sorgusu üreten saf builder. Grup özeti, top-N ve
// zaman serisi bu tek desenin türevleridir. Güvenlik DatasetSqlExpr ile ortak (whitelist +
// parametre + escape); işlem/bucket/sort de whitelist.
public static class DatasetAggregateQueryBuilder
{
    private static readonly HashSet<string> Ops = new() { "count", "sum", "avg", "min", "max" };
    private static readonly HashSet<string> Buckets = new() { "day", "week", "month", "year" };

    public static BuiltAggregate Build(AggregateQuery q, IReadOnlyDictionary<string, string> schema)
    {
        if (!schema.TryGetValue(q.GroupBy, out var groupType))
            throw new InvalidQueryException($"Bilinmeyen kolon: {q.GroupBy}");

        var op = (q.Op ?? "").ToLowerInvariant();
        if (!Ops.Contains(op))
            throw new InvalidQueryException($"Bilinmeyen işlem: {q.Op}. (count/sum/avg/min/max)");

        // Grup ifadesi: bucket varsa date_trunc (zaman serisi), yoksa tipli değer.
        string groupExpr;
        if (!string.IsNullOrWhiteSpace(q.Bucket))
        {
            if (groupType != "date")
                throw new InvalidQueryException("bucket yalnızca tarih kolonlarında kullanılır.");
            var b = q.Bucket.ToLowerInvariant();
            if (!Buckets.Contains(b))
                throw new InvalidQueryException($"Bilinmeyen bucket: {q.Bucket}. (day/week/month/year)");
            groupExpr = $"date_trunc('{b}', {DatasetSqlExpr.Typed(q.GroupBy, "date")})";
        }
        else
        {
            groupExpr = DatasetSqlExpr.Typed(q.GroupBy, groupType);
        }

        // Agregasyon ifadesi. count metric istemez; diğerleri sayısal metric ister.
        string aggExpr;
        if (op == "count")
        {
            aggExpr = "COUNT(*)::numeric";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(q.Metric))
                throw new InvalidQueryException($"'{op}' için metric kolonu gerekli.");
            if (!schema.TryGetValue(q.Metric, out var metricType))
                throw new InvalidQueryException($"Bilinmeyen metric kolonu: {q.Metric}");
            if (metricType != "number")
                throw new InvalidQueryException($"'{op}' yalnızca sayısal kolonlarda kullanılır: {q.Metric}");

            var m = DatasetSqlExpr.Typed(q.Metric, "number");
            aggExpr = op switch
            {
                "sum" => $"SUM({m})",
                "avg" => $"AVG({m})",
                "min" => $"MIN({m})",
                "max" => $"MAX({m})",
                _ => throw new InvalidQueryException($"Bilinmeyen işlem: {op}")
            };
        }

        var (where, parameters) = DatasetSqlExpr.BuildWhere(q.Filters, schema);

        // Sıralama: value → agregasyon değeri, key → grup anahtarı (tipli, doğru sıralama).
        var sortBy = (q.Sort ?? "key").ToLowerInvariant();
        if (sortBy is not ("key" or "value"))
            throw new InvalidQueryException($"Bilinmeyen sıralama: {q.Sort}. (key/value)");
        var dir = string.Equals(q.Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
        var orderExpr = sortBy == "value" ? "\"Value\"" : groupExpr;

        // Limit doğrulanmış tamsayı → doğrudan gömmek güvenli (injection yok). Top-N için.
        var limitSql = q.Limit is int lim ? $" LIMIT {Math.Clamp(lim, 1, 1000)}" : "";

        // Key text'e cast edilir → tek tip materializasyon; ORDER BY yine tipli groupExpr ile.
        var sql = $"""
            SELECT ({groupExpr})::text AS "Key", {aggExpr} AS "Value", COUNT(*)::int AS "Count"
            FROM "DatasetRows"
            WHERE "DatasetId" = @datasetId{where}
            GROUP BY {groupExpr}
            ORDER BY {orderExpr} {dir}{limitSql}
            """;

        return new BuiltAggregate(sql, parameters);
    }
}
