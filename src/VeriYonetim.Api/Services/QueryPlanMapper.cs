namespace VeriYonetim.Api.Services;

// Modelin ürettiği planı, builder'ların anladığı doğrulanmış isteklere çevirir.
//
// GÜVENLİK SINIRI BURASI. Bu noktanın öncesinde "modelden gelen serbest metin" vardır;
// sonrasında yalnızca whitelist'ten geçmiş kolon adları, sabit fiiller ve parametreler
// kalır. Eksik/uydurma bir alan burada ya da builder'da InvalidQueryException'a dönüşür,
// yani kullanıcı sessiz yanlış cevap değil anlaşılır bir hata görür.
public static class QueryPlanMapper
{
    // Doğal dil sorgusunda sayfalama yoktur: soru ("en pahalı 5 ürün") tek seferliktir.
    public const int DefaultRowLimit = 50;
    public const int MaxRowLimit = 200;

    public static readonly IReadOnlyList<string> Kinds = new[] { "rows", "aggregate", "unsupported" };

    // Planın dokunduğu veri setleri: önce from, sonra join'dekiler (sıra JOIN kurulumunda
    // önemli — ilk kaynak FROM'a, diğerleri sırayla JOIN'e yazılır).
    public static IReadOnlyList<string> DatasetNames(QueryPlan plan)
    {
        var names = new List<string>();

        if (!string.IsNullOrWhiteSpace(plan.From))
            names.Add(plan.From.Trim());

        foreach (var name in plan.Join ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());

        return names;
    }

    // Model "Aggregate" / " rows " gibi yazabilir; karşılaştırma öncesi normalleştirilir.
    public static string NormalizeKind(QueryPlan plan)
    {
        var kind = (plan.Kind ?? "").Trim().ToLowerInvariant();

        if (kind.Length == 0)
            throw new InvalidQueryException(
                "Plan türü (kind) belirtilmemiş. (rows/aggregate/unsupported)");

        if (!Kinds.Contains(kind))
            throw new InvalidQueryException(
                $"Bilinmeyen plan türü: {plan.Kind}. (rows/aggregate/unsupported)");

        return kind;
    }

    public static RowQuery ToRowQuery(QueryPlan plan)
    {
        var limit = Math.Clamp(plan.Limit ?? DefaultRowLimit, 1, MaxRowLimit);

        // Sayfa daima 1: liste sorusu tek sayfalık bir cevaptır, gezinme değil.
        return new RowQuery(1, limit, plan.Sort, plan.Dir, MapFilters(plan.Filters));
    }

    public static AggregateQuery ToAggregateQuery(QueryPlan plan)
    {
        // Ölçüm eksikse VARSAYILAN ATAMIYORUZ. "Adet say" diye tahmin etseydik kullanıcının
        // sormadığı bir soruyu cevaplamış olurduk; builder anlaşılır bir hata veriyor.
        var metrics = (plan.Metrics ?? Array.Empty<PlanMetric>())
            .Select(m => new MetricSpec(m.Op ?? "", m.Column))
            .ToList();

        var having = plan.Having is { } h ? new HavingSpec(h.Metric, h.Op ?? "", h.Value) : null;

        return new AggregateQuery(
            GroupBy: plan.GroupBy ?? Array.Empty<string>(),
            Metrics: metrics,
            Bucket: plan.Bucket,
            Sort: plan.Sort,
            Dir: plan.Dir,
            Limit: plan.Limit,
            Filters: MapFilters(plan.Filters),
            Having: having,
            SortMetric: plan.SortMetric,
            Share: plan.Share,
            ShareMetric: plan.ShareMetric);
    }

    // Dönem karşılaştırma için aynı agregasyonun iki dönemlik sürümünü üretir.
    //
    // Limit İKİSİNDEN DE çıkarılır: her döneme ayrı ayrı "ilk 5" uygulansaydı iki farklı
    // beşli küme çıkar, eşleştirme sonrası 10 satırlık tuhaf bir liste oluşurdu. Limit,
    // sonuçlar eşleştirildikten SONRA uygulanır (bkz. çağıran uç).
    public static (AggregateQuery Current, AggregateQuery Previous) ToComparisonQueries(QueryPlan plan)
    {
        var compare = plan.Compare
            ?? throw new InvalidQueryException("Karşılaştırma için compare alanı gerekli.");

        if (string.IsNullOrWhiteSpace(compare.Column))
            throw new InvalidQueryException("Karşılaştırma için tarih kolonu (compare.column) gerekli.");
        if (string.IsNullOrWhiteSpace(compare.Period) || string.IsNullOrWhiteSpace(compare.Previous))
            throw new InvalidQueryException(
                "Karşılaştırma için iki dönem gerekli (compare.period ve compare.previous).");

        var baseQuery = ToAggregateQuery(plan) with { Limit = null };

        return (WithPeriod(baseQuery, compare.Column, compare.Period),
                WithPeriod(baseQuery, compare.Column, compare.Previous));
    }

    private static AggregateQuery WithPeriod(AggregateQuery query, string column, string period)
    {
        var filters = query.Filters.ToList();
        filters.Add(new RowFilter(column, "inPeriod", period));
        return query with { Filters = filters };
    }

    private static readonly System.Text.RegularExpressions.Regex YearPattern =
        new(@"\b(19|20)\d{2}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DatePattern =
        new(@"\d{4}-\d{2}-\d{2}", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Soruda açıkça geçen ama plana HİÇ yansımayan tarih koşulunu yakalar.
    //
    // Neden gerekli? Model bir koşulu sessizce atlayabilir: "2023 yılında toplam satış"
    // sorusuna filtresiz bir plan üretirse sonuç BÜTÜN yılların toplamı olur. Sorgu
    // çalışır, hata çıkmaz, grafik çizilir — ve kullanıcı yanlış sayıya bakar. Sistemin
    // verebileceği en kötü cevap türü budur; sessiz olduğu için fark edilmez.
    //
    // Kontrol bilinçli olarak GEVŞEK: planda herhangi bir tarih koşulu varsa ona güveniyoruz.
    // Amaç "yanlış aralık seçilmiş mi" değil, "koşul tümden düşmüş mü" sorusunu yakalamak.
    public static void ValidateAgainstQuestion(QueryPlan plan, string question)
    {
        if (!YearPattern.IsMatch(question)) return;

        // Dönem karşılaştırması zaten tarihe dayanır.
        if (plan.Compare is not null) return;

        var values = new List<string>();
        CollectFilterValues(plan.Filters, values);

        var hasDateCondition = values.Any(v =>
            DatePattern.IsMatch(v) || RelativePeriod.Tokens.Contains(v));

        if (hasDateCondition) return;

        var year = YearPattern.Match(question).Value;
        throw new InvalidQueryException(
            $"Soruda geçen {year} yılı için bir tarih koşulu üretilemedi. " +
            "Filtresiz bir toplam göstermek yanıltıcı olurdu; soruyu biraz farklı yazmayı deneyin.");
    }

    private static void CollectFilterValues(IReadOnlyList<PlanFilter>? filters, List<string> into)
    {
        foreach (var f in filters ?? Array.Empty<PlanFilter>())
        {
            if (!string.IsNullOrWhiteSpace(f.Logic))
            {
                CollectFilterValues(f.Children, into);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(f.Value)) into.Add(f.Value.Trim());
            foreach (var v in f.Values ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(v)) into.Add(v.Trim());
        }
    }

    // Planın dokunduğu bütün kolon referansları.
    //
    // Ne işe yarar? Model gerekli veri setini join'e eklemeyi unutabilir ("şehirlere göre
    // toplam satış" — şehir Müşteriler'de). Hangi kolonların istendiğini bilirsek eksik
    // seti kendimiz ekleyebiliriz (bkz. TenantCatalog.BuildScope).
    public static IReadOnlyList<string> ColumnReferences(QueryPlan plan)
    {
        var refs = new List<string>();

        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name)) refs.Add(name.Trim());
        }

        foreach (var g in plan.GroupBy ?? Array.Empty<string>()) Add(g);
        foreach (var s in plan.Select ?? Array.Empty<string>()) Add(s);
        foreach (var m in plan.Metrics ?? Array.Empty<PlanMetric>()) Add(m.Column);
        Add(plan.Sort);
        Add(plan.Compare?.Column);
        CollectFilterColumns(plan.Filters, refs);

        return refs;
    }

    private static void CollectFilterColumns(IReadOnlyList<PlanFilter>? filters, List<string> into)
    {
        foreach (var f in filters ?? Array.Empty<PlanFilter>())
        {
            if (!string.IsNullOrWhiteSpace(f.Logic))
                CollectFilterColumns(f.Children, into);
            else if (!string.IsNullOrWhiteSpace(f.Column))
                into.Add(f.Column.Trim());
        }
    }

    public static IReadOnlyList<FilterNode> MapFilters(IReadOnlyList<PlanFilter>? filters) =>
        (filters ?? Array.Empty<PlanFilter>()).Select(MapFilter).ToList();

    // Yaprak mı grup mu? Ayrımı Logic alanının varlığı yapar.
    // Derinlik ve genişlik sınırları burada değil DatasetSqlExpr'de uygulanır — sınır,
    // planın nereden geldiğinden bağımsız olarak SQL üretiminin kendi güvencesi olmalı.
    private static FilterNode MapFilter(PlanFilter f)
    {
        if (!string.IsNullOrWhiteSpace(f.Logic))
            return new FilterGroup(
                f.Logic,
                (f.Children ?? Array.Empty<PlanFilter>()).Select(MapFilter).ToList());

        if (string.IsNullOrWhiteSpace(f.Column))
            throw new InvalidQueryException("Filtrede kolon adı eksik.");
        if (string.IsNullOrWhiteSpace(f.Op))
            throw new InvalidQueryException($"'{f.Column}' filtresinde operatör eksik.");

        return new RowFilter(f.Column, f.Op, f.Value ?? "", f.Values);
    }
}
