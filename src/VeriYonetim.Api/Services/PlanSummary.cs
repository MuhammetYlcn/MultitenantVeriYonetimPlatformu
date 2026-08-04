using System.Text;

namespace VeriYonetim.Api.Services;

// Planı kullanıcının okuyabileceği tek cümleye çevirir.
//
// Neden gerekli? Üretilen SQL'i göstermek uzman için yeterli ama çoğu kullanıcı SQL
// okuyamaz. "Anladığım sorgu" satırı, modelin soruyu YANLIŞ anladığı durumu kullanıcının
// sonuca bakmadan fark etmesini sağlar — sessiz yanlış cevaba karşı son savunma hattı.
public static class PlanSummary
{
    public static string Describe(QueryPlan plan, IReadOnlyList<string> datasets)
    {
        var kind = (plan.Kind ?? "").Trim().ToLowerInvariant();

        if (kind == "unsupported")
            return plan.Reason ?? "Bu soru cevaplanamıyor.";

        var sb = new StringBuilder();

        sb.Append(datasets.Count > 1
            ? $"{string.Join(" + ", datasets)} birleştirilerek"
            : datasets.FirstOrDefault() ?? "veri seti");

        if (kind == "rows")
        {
            sb.Append(" satırları listelenir");

            if (!string.IsNullOrWhiteSpace(plan.Sort))
                sb.Append($"; {plan.Sort} göre {(IsDesc(plan.Dir) ? "azalan" : "artan")} sırada");

            if (plan.Limit is int rowLimit)
                sb.Append($"; ilk {rowLimit}");
        }
        else
        {
            AppendMetrics(sb, plan);
            AppendGrouping(sb, plan);

            if (plan.Having is { } having)
                sb.Append($"; yalnız {Operator(having.Op)} {having.Value} olan gruplar");

            if (plan.Share)
                sb.Append("; toplam içindeki yüzdesiyle");

            if (plan.Compare is { } compare)
                sb.Append($"; {Period(compare.Period)} ile {Period(compare.Previous)} karşılaştırılır");

            if (plan.Limit is int limit)
                sb.Append($"; ilk {limit}");
        }

        AppendFilters(sb, plan.Filters);

        return sb.ToString();
    }

    private static void AppendMetrics(StringBuilder sb, QueryPlan plan)
    {
        var metrics = plan.Metrics ?? Array.Empty<PlanMetric>();
        if (metrics.Count == 0) return;

        var parts = metrics.Select(m => string.IsNullOrWhiteSpace(m.Column)
            ? MetricName(m.Op)
            : $"{m.Column} {MetricName(m.Op)}");

        sb.Append($" {string.Join(", ", parts)}");
    }

    private static void AppendGrouping(StringBuilder sb, QueryPlan plan)
    {
        var groupBy = plan.GroupBy ?? Array.Empty<string>();
        if (groupBy.Count == 0)
        {
            sb.Append(" (tüm kayıtlar için tek değer)");
            return;
        }

        sb.Append($", {string.Join(" ve ", groupBy)} bazında");

        if (!string.IsNullOrWhiteSpace(plan.Bucket))
            sb.Append($" ({BucketName(plan.Bucket)} olarak)");
    }

    private static void AppendFilters(StringBuilder sb, IReadOnlyList<PlanFilter>? filters)
    {
        if (filters is not { Count: > 0 }) return;

        var parts = filters.Select(DescribeFilter).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return;

        sb.Append($". Koşul: {string.Join(" ve ", parts)}");
    }

    private static string DescribeFilter(PlanFilter f)
    {
        if (!string.IsNullOrWhiteSpace(f.Logic))
        {
            var children = (f.Children ?? Array.Empty<PlanFilter>()).Select(DescribeFilter);
            var joiner = f.Logic.Trim().Equals("or", StringComparison.OrdinalIgnoreCase) ? " veya " : " ve ";
            return $"({string.Join(joiner, children)})";
        }

        var op = (f.Op ?? "").Trim();

        return op switch
        {
            "isNull" => $"{f.Column} boş",
            "notNull" => $"{f.Column} dolu",
            "in" => $"{f.Column} ∈ [{string.Join(", ", f.Values ?? Array.Empty<string>())}]",
            "notIn" => $"{f.Column} ∉ [{string.Join(", ", f.Values ?? Array.Empty<string>())}]",
            "inPeriod" => $"{f.Column} {Period(f.Value)} içinde",
            "contains" => $"{f.Column} '{f.Value}' içeriyor",
            _ => $"{f.Column} {Operator(op)} {f.Value}"
        };
    }

    private static bool IsDesc(string? dir) =>
        string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);

    private static string MetricName(string? op) => (op ?? "").Trim().ToLowerInvariant() switch
    {
        "count" => "adedi",
        "sum" => "toplamı",
        "avg" => "ortalaması",
        "min" => "en düşüğü",
        "max" => "en yükseği",
        "countdistinct" => "farklı değer sayısı",
        _ => op ?? "?"
    };

    private static string Operator(string? op) => (op ?? "").Trim() switch
    {
        "eq" => "=",
        "ne" => "≠",
        "gt" => ">",
        "gte" => "≥",
        "lt" => "<",
        "lte" => "≤",
        _ => op ?? "?"
    };

    private static string BucketName(string? bucket) => (bucket ?? "").Trim().ToLowerInvariant() switch
    {
        "day" => "günlük",
        "week" => "haftalık",
        "month" => "aylık",
        "year" => "yıllık",
        _ => bucket ?? ""
    };

    private static string Period(string? token) => (token ?? "").Trim() switch
    {
        "bugun" => "bugün",
        "dun" => "dün",
        "buHafta" => "bu hafta",
        "gecenHafta" => "geçen hafta",
        "son7Gun" => "son 7 gün",
        "son30Gun" => "son 30 gün",
        "son90Gun" => "son 90 gün",
        "buAy" => "bu ay",
        "gecenAy" => "geçen ay",
        "son12Ay" => "son 12 ay",
        "buCeyrek" => "bu çeyrek",
        "gecenCeyrek" => "geçen çeyrek",
        "buYil" => "bu yıl",
        "gecenYil" => "geçen yıl",
        _ => token ?? ""
    };
}
