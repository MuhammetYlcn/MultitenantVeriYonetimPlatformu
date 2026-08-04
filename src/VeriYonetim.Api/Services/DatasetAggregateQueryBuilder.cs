using Npgsql;
using NpgsqlTypes;

namespace VeriYonetim.Api.Services;

// Tek bir ölçüm: işlem + (gerekiyorsa) kolon. count kolon istemez; sum/avg/min/max sayısal
// kolon ister; countDistinct her tipte çalışır ("kaç farklı şehir" metin kolonu sorar).
public record MetricSpec(string Op, string? Column = null);

// Agregasyon SONUCUNA uygulanan koşul. WHERE'den farkı şu: WHERE tek tek satırlara bakar,
// HAVING gruplama bittikten sonra grubun kendisine bakar. "Ortalaması 1000 üstü şehirler"
// WHERE ile ifade EDİLEMEZ — çünkü koşul satırın değil, grubun ortalamasıyla ilgilidir.
// Metric: Metrics listesindeki ölçümün sırası (0 tabanlı).
public record HavingSpec(int Metric, string Op, decimal Value);

// Agregasyon isteğinin parçaları.
// GroupBy boş → gruplamasız genel agregasyon (KPI kartı). Metrics en az bir ölçüm içermeli.
// Bucket yalnızca İLK gruplama kolonuna uygulanır ve o kolon tarih olmalıdır.
public record AggregateQuery(
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<MetricSpec> Metrics,
    string? Bucket,
    string? Sort,
    string? Dir,
    int? Limit,
    IReadOnlyList<FilterNode> Filters,
    HavingSpec? Having = null,
    int SortMetric = 0,
    bool Share = false,
    int ShareMetric = 0)
{
    // Tek gruplama + tek ölçümlü eski çağrı biçimi. Querystring ucu (/aggregate) bunu
    // kullanmaya devam eder; doğal dil tarafı ise zengin biçimi kullanır.
    public AggregateQuery(string? groupBy, string op, string? metric, string? bucket,
        string? sort, string? dir, int? limit, IReadOnlyList<FilterNode> filters)
        : this(
            string.IsNullOrWhiteSpace(groupBy) ? Array.Empty<string>() : new[] { groupBy },
            new[] { new MetricSpec(op, metric) },
            bucket, sort, dir, limit, filters)
    { }
}

// Build çıktısı: tam SQL (@datasetId + @f0.. + @h0 yer tutucularıyla) ve parametreler.
// KeyCount/MetricCount okuyucuya kolon düzenini söyler: önce anahtarlar, sonra ölçümler,
// en sonda Count. Çağıran sütun sırasını tahmin etmek zorunda kalmasın diye taşınıyor.
public record BuiltAggregate(
    string Sql,
    IReadOnlyList<NpgsqlParameter> Parameters,
    int KeyCount,
    int MetricCount,
    bool HasShare = false);

// JSONB üzerinde GROUP BY + agregasyon sorgusu üreten saf builder. Grup özeti, top-N ve
// zaman serisi bu tek desenin türevleridir. Güvenlik DatasetSqlExpr ile ortak (whitelist +
// parametre + escape); işlem/bucket/sort de whitelist.
public static class DatasetAggregateQueryBuilder
{
    private static readonly HashSet<string> Buckets = new() { "day", "week", "month", "year" };

    // İşlem whitelist'i (küçük harfe indirgenmiş halleriyle karşılaştırılır).
    private static readonly HashSet<string> Ops =
        new() { "count", "sum", "avg", "min", "max", "countdistinct" };

    // Üst sınırlar: doğal dilden gelen bir plan bunları aşıyorsa büyük ihtimalle hatalıdır.
    // Sınır olmadan model "her kolona göre grupla" diyerek okunamaz bir sonuç üretebilir.
    private const int MaxGroupBy = 3;
    private const int MaxMetrics = 6;

    public static BuiltAggregate Build(AggregateQuery q, IReadOnlyDictionary<string, string> schema)
    {
        var groupBy = q.GroupBy ?? Array.Empty<string>();
        var metrics = q.Metrics ?? Array.Empty<MetricSpec>();

        var groupExprs = BuildGroupExpressions(groupBy, q.Bucket, schema);
        var aggExprs = BuildMetricExpressions(metrics, schema);

        var (where, parameters) = DatasetSqlExpr.BuildWhere(
            q.Filters ?? Array.Empty<FilterNode>(), schema);

        // Grup anahtarları text'e cast edilir (tek tip materializasyon). Gruplamasız durumda
        // tek bir NULL anahtar yazılır ki okuyucunun sütun düzeni her iki halde de aynı olsun.
        var keySelect = groupExprs.Count == 0
            ? "NULL::text AS \"Key0\""
            : string.Join(", ", groupExprs.Select((e, i) => $"({e})::text AS \"Key{i}\""));

        var valueSelect = string.Join(", ", aggExprs.Select((e, i) => $"{e} AS \"Value{i}\""));

        var groupBySql = groupExprs.Count == 0
            ? ""
            : $"\nGROUP BY {string.Join(", ", groupExprs)}";

        var havingSql = BuildHaving(q.Having, aggExprs, groupExprs.Count, parameters);
        var orderLimitSql = BuildOrderLimit(q, groupExprs, aggExprs.Count);
        var shareSql = BuildShare(q, metrics, aggExprs, groupExprs.Count);

        // Share bilinçli olarak EN SONA yazılır: okuyucu anahtar/ölçüm/Count indekslerini
        // pay'ın varlığına göre kaydırmak zorunda kalmasın.
        var sql = $"""
            SELECT {keySelect}, {valueSelect}, COUNT(*)::int AS "Count"{shareSql}
            FROM "DatasetRows"
            WHERE "DatasetId" = @datasetId{where}{groupBySql}{havingSql}{orderLimitSql}
            """;

        return new BuiltAggregate(sql, parameters, Math.Max(groupExprs.Count, 1), aggExprs.Count,
            HasShare: shareSql.Length > 0);
    }

    // Gruplama ifadeleri. Bucket yalnızca ilk kolona uygulanır: "aylara ve şehre göre"
    // sorusunda kovalanacak olan tarih kolonudur, şehir değil.
    private static List<string> BuildGroupExpressions(
        IReadOnlyList<string> groupBy, string? bucket, IReadOnlyDictionary<string, string> schema)
    {
        if (groupBy.Count > MaxGroupBy)
            throw new InvalidQueryException($"En fazla {MaxGroupBy} kolona göre gruplanabilir.");

        // Aynı kolonla iki kez gruplamak sessizce anlamsız bir sonuç üretirdi.
        if (groupBy.Distinct().Count() != groupBy.Count)
            throw new InvalidQueryException("Aynı kolon birden çok kez gruplanamaz.");

        var hasBucket = !string.IsNullOrWhiteSpace(bucket);
        if (hasBucket && groupBy.Count == 0)
            throw new InvalidQueryException("bucket için groupBy (tarih kolonu) gerekli.");

        var exprs = new List<string>(groupBy.Count);

        for (var i = 0; i < groupBy.Count; i++)
        {
            var col = groupBy[i];
            if (!schema.TryGetValue(col, out var type))
                throw new InvalidQueryException($"Bilinmeyen kolon: {col}");

            if (i == 0 && hasBucket)
            {
                if (type != "date")
                    throw new InvalidQueryException("bucket yalnızca tarih kolonlarında kullanılır.");

                var b = bucket!.ToLowerInvariant();
                if (!Buckets.Contains(b))
                    throw new InvalidQueryException($"Bilinmeyen bucket: {bucket}. (day/week/month/year)");

                exprs.Add($"date_trunc('{b}', {DatasetSqlExpr.Typed(col, "date")})");
            }
            else
            {
                exprs.Add(DatasetSqlExpr.Typed(col, type));
            }
        }

        return exprs;
    }

    private static List<string> BuildMetricExpressions(
        IReadOnlyList<MetricSpec> metrics, IReadOnlyDictionary<string, string> schema)
    {
        if (metrics.Count == 0)
            throw new InvalidQueryException("En az bir ölçüm (op) gerekli.");
        if (metrics.Count > MaxMetrics)
            throw new InvalidQueryException($"En fazla {MaxMetrics} ölçüm hesaplanabilir.");

        return metrics.Select(m => BuildMetric(m, schema)).ToList();
    }

    private static string BuildMetric(MetricSpec m, IReadOnlyDictionary<string, string> schema)
    {
        var op = (m.Op ?? "").Trim().ToLowerInvariant();

        // İşlem whitelist'i ÖNCE denetlenir: aksi halde bilinmeyen bir işlem "kolon gerekli"
        // gibi yanıltıcı bir hata alır ve asıl sorun (uydurulmuş işlem) gizlenirdi.
        if (!Ops.Contains(op))
            throw new InvalidQueryException(
                $"Bilinmeyen işlem: {m.Op}. (count/sum/avg/min/max/countDistinct)");

        // COUNT(*) satır sayar; hiçbir kolona ihtiyaç duymaz.
        if (op == "count") return "COUNT(*)::numeric";

        if (string.IsNullOrWhiteSpace(m.Column))
            throw new InvalidQueryException($"'{m.Op}' için metric kolonu gerekli.");
        if (!schema.TryGetValue(m.Column, out var type))
            throw new InvalidQueryException($"Bilinmeyen metric kolonu: {m.Column}");

        // COUNT(DISTINCT) benzersiz DEĞER sayar — COUNT(*)'tan bambaşka bir soru.
        // Sayısal kısıtı yok: "kaç farklı şehir" sorusu metin kolonu üzerinde sorulur.
        if (op == "countdistinct")
            return $"COUNT(DISTINCT {DatasetSqlExpr.Typed(m.Column, type)})::numeric";

        if (type != "number")
            throw new InvalidQueryException($"'{m.Op}' yalnızca sayısal kolonlarda kullanılır: {m.Column}");

        var expr = DatasetSqlExpr.Typed(m.Column, "number");
        return op switch
        {
            "sum" => $"SUM({expr})",
            "avg" => $"AVG({expr})",
            "min" => $"MIN({expr})",
            "max" => $"MAX({expr})",
            _ => throw new InvalidQueryException(
                $"Bilinmeyen işlem: {m.Op}. (count/sum/avg/min/max/countDistinct)")
        };
    }

    private static string BuildHaving(
        HavingSpec? having, List<string> aggExprs, int groupCount, List<NpgsqlParameter> parameters)
    {
        if (having is not { } h) return "";

        // Gruplama yoksa zaten tek satır döner; HAVING o satırı ya tümden eler ya bırakır —
        // anlamsız olduğu için erken reddediyoruz.
        if (groupCount == 0)
            throw new InvalidQueryException("having için groupBy gerekli.");

        if (h.Metric < 0 || h.Metric >= aggExprs.Count)
            throw new InvalidQueryException(
                $"having, olmayan bir ölçümü işaret ediyor: {h.Metric}.");

        if (!DatasetSqlExpr.Operators.TryGetValue((h.Op ?? "").Trim(), out var sqlOp))
            throw new InvalidQueryException($"having için bilinmeyen operatör: {h.Op}");

        parameters.Add(new NpgsqlParameter("h0", NpgsqlDbType.Numeric) { Value = h.Value });

        // Alias DEĞİL, ifadenin kendisi tekrarlanır: PostgreSQL HAVING içinde SELECT
        // takma adlarını kabul etmez (ORDER BY'ın aksine).
        return $"\nHAVING {aggExprs[h.Metric]} {sqlOp} @h0";
    }

    // Grubun toplam içindeki yüzdesi. "Her şehrin ciro içindeki payı" sorusunun karşılığı.
    //
    // SUM(...) OVER () bir PENCERE fonksiyonudur: agregasyon bittikten sonra tüm grupların
    // sonuçlarını yeniden toplar. Yani payda "tablonun toplamı" değil, "bu sorgunun döndürdüğü
    // grupların toplamı"dır — filtre ya da HAVING varsa yüzdeler onlara göre hesaplanır.
    private static string BuildShare(
        AggregateQuery q, IReadOnlyList<MetricSpec> metrics, List<string> aggExprs, int groupCount)
    {
        if (!q.Share) return "";

        if (groupCount == 0)
            throw new InvalidQueryException(
                "share için groupBy gerekli: pay, bir grubun bütün içindeki oranıdır.");

        if (q.ShareMetric < 0 || q.ShareMetric >= aggExprs.Count)
            throw new InvalidQueryException(
                $"share, olmayan bir ölçümü işaret ediyor: {q.ShareMetric}.");

        // Pay yalnızca TOPLANABİLİR ölçümlerde anlamlıdır. Ortalamaların toplamı bir bütün
        // etmez; "ortalamanın yüzdesi" diye bir şey yoktur. Sessiz saçmalık üretmek yerine
        // açıkça reddediyoruz.
        var op = (metrics[q.ShareMetric].Op ?? "").Trim().ToLowerInvariant();
        if (op is not ("count" or "sum"))
            throw new InvalidQueryException(
                $"Pay/yüzde yalnızca count ve sum ölçümlerinde hesaplanır: {metrics[q.ShareMetric].Op}");

        var expr = aggExprs[q.ShareMetric];

        // NULLIF: toplam 0 ise sıfıra bölme hatası yerine NULL döner.
        return $", ({expr} * 100.0 / NULLIF(SUM({expr}) OVER (), 0)) AS \"Share\"";
    }

    private static string BuildOrderLimit(AggregateQuery q, List<string> groupExprs, int metricCount)
    {
        // Gruplamasızda tek satır döner; sıralama/limit gereksiz.
        if (groupExprs.Count == 0) return "";

        var sortBy = (q.Sort ?? "key").ToLowerInvariant();
        if (sortBy is not ("key" or "value"))
            throw new InvalidQueryException($"Bilinmeyen sıralama: {q.Sort}. (key/value)");

        var dir = string.Equals(q.Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";

        string orderExpr;
        if (sortBy == "value")
        {
            // Çoklu ölçümde hangisine göre sıralanacağı belirsiz kalmasın diye açık indeks.
            if (q.SortMetric < 0 || q.SortMetric >= metricCount)
                throw new InvalidQueryException(
                    $"sortMetric, olmayan bir ölçümü işaret ediyor: {q.SortMetric}.");
            orderExpr = $"\"Value{q.SortMetric}\"";
        }
        else
        {
            // Anahtara göre sıralama: tipli ifadelerle (metinsel değil kronolojik/sayısal).
            orderExpr = string.Join(", ", groupExprs.Select(e => $"{e} {dir}"));
            var limitByKey = q.Limit is int lk ? $" LIMIT {Math.Clamp(lk, 1, 1000)}" : "";
            return $"\nORDER BY {orderExpr}{limitByKey}";
        }

        var limitSql = q.Limit is int lim ? $" LIMIT {Math.Clamp(lim, 1, 1000)}" : "";
        return $"\nORDER BY {orderExpr} {dir}{limitSql}";
    }
}
