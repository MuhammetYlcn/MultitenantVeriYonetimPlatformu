using VeriYonetim.Api.Models.Dtos;

namespace VeriYonetim.Api.Services;

/// <summary>
/// Bir izleyici koşusunun sonucu: ölçülen değer + planın Türkçe özeti + kullanılan setler.
/// </summary>
public record WatchMeasurement(decimal? Value, string Summary, IReadOnlyList<string> Datasets);

/// <summary>
/// Kaydedilmiş bir planı çalıştırıp TEK BİR SAYI üretir — izleyicinin ölçüm katmanı.
///
/// Neden tek sayı: bir eşik ancak tek bir sayıyla karşılaştırılabilir. Gruplu bir sonuç
/// ("şehirlere göre ciro") on beş sayıdır ve "eşik aşıldı" cümlesinin öznesi belirsiz
/// kalır; dönem karşılaştırması ise iki sayıdır. Bunları zorlayarak tek sayıya indirmek
/// (ilkini al, en büyüğünü al) kullanıcının sormadığı bir soruyu cevaplamak olurdu.
/// Bu yüzden izlenemeyen plan sessizce daraltılmıyor, KURULMA anında reddediliyor —
/// kullanıcı neyi izleyemediğini o an öğreniyor, aylar sonra yanlış bir sayıya bakarak değil.
/// </summary>
public interface IWatchEvaluator
{
    /// <summary>
    /// Planı çalıştırır. Katalog/plan tutarsızsa <see cref="InvalidQueryException"/> fırlatır —
    /// çağıran bunu "izleyici kırıldı" olarak işaretler, sıfır değer olarak DEĞİL.
    /// </summary>
    Task<WatchMeasurement> MeasureAsync(QueryPlan plan, CancellationToken ct = default);
}

public class WatchEvaluator : IWatchEvaluator
{
    private readonly ITenantCatalogLoader _catalogLoader;
    private readonly IDatasetQueryExecutor _executor;

    public WatchEvaluator(ITenantCatalogLoader catalogLoader, IDatasetQueryExecutor executor)
    {
        _catalogLoader = catalogLoader;
        _executor = executor;
    }

    /// <summary>
    /// Plan izlenebilir mi. İzlenemiyorsa sebebi kullanıcının anlayacağı dilde döner;
    /// null dönmesi "izlenebilir" demektir.
    ///
    /// Bu denetim kaydetme anında çalışır. Sonradan çalıştırmak da mümkündü ama o zaman
    /// kullanıcı izleyiciyi kurar, haftalarca hiçbir uyarı almaz ve sebebini bilmezdi.
    /// </summary>
    public static string? DescribeUnwatchable(QueryPlan plan)
    {
        var kind = QueryPlanMapper.NormalizeKind(plan);

        if (kind == "unsupported")
            return "Bu soru cevaplanamadığı için izlenemez.";

        if (plan.Compare is not null)
            return "Dönem karşılaştırması izlenemez: iki dönemin iki ayrı sayısı vardır, " +
                   "eşik ise tek bir sayıyla karşılaştırılır.";

        if (kind == "rows")
            // Satır listesi tek sayı değil, ama SAYISI tek sayıdır ve izlemenin doğal
            // karşılığı budur: "stoğu biten ürün var mı" sorusu "kaç tane" ile ölçülür.
            return null;

        if ((plan.GroupBy?.Count ?? 0) > 0)
            return "Gruplanmış sonuç izlenemez: her grup ayrı bir sayı üretir. " +
                   "Tek bir toplam veren bir soru sorun (örneğin gruplamadan toplam).";

        if ((plan.Metrics?.Count ?? 0) == 0)
            return "Bu sorunun ölçülen bir değeri yok, bu yüzden izlenemez.";

        return null;
    }

    public async Task<WatchMeasurement> MeasureAsync(QueryPlan plan, CancellationToken ct = default)
    {
        if (DescribeUnwatchable(plan) is { } reason)
            throw new InvalidQueryException(reason);

        // Katalog HER KOŞUDA yeniden okunuyor, kurulduğu andaki hâli saklanmıyor.
        // Bilinçli: izleyicinin dayandığı kolon silinmişse bunu öğrenmenin tek yolu
        // güncel kataloğa bakmak. Donmuş bir katalogla çalışsaydı plan sonsuza kadar
        // "geçerli" görünür, ama sorgu her koşuda düşerdi.
        var catalog = await _catalogLoader.LoadAsync(ct);

        var datasetNames = QueryPlanMapper.DatasetNames(plan);
        var scope = catalog.BuildScope(datasetNames, QueryPlanMapper.ColumnReferences(plan));
        var used = scope.Sources.Select(s => s.Name).ToList();
        var summary = PlanSummary.Describe(plan, used);

        var query = QueryPlanMapper.NormalizeKind(plan) == "rows"
            ? ToCountQuery(plan)
            : ToScalarQuery(plan);

        var built = DatasetAggregateQueryBuilder.Build(query, scope);
        var buckets = await _executor.RunAggregateAsync(built, ct: ct);

        return new WatchMeasurement(ReadValue(query, buckets), summary, used);
    }

    /// Satır listesi sorgusu → aynı filtrelerle satır SAYISI.
    ///
    /// Limit bilinçli olarak düşürülüyor: "ilk 10 ürünü göster" planında 10, sorunun
    /// cevabının kaç satır olduğunu değil kaç satırın GÖSTERİLECEĞİNİ söyler. İzleyici
    /// eşiği gerçek sayıya bakmalı, ekrana sığan kadarına değil.
    private static AggregateQuery ToCountQuery(QueryPlan plan) => new(
        GroupBy: Array.Empty<string>(),
        Metrics: new[] { new MetricSpec("count") },
        Bucket: null,
        Sort: null,
        Dir: null,
        Limit: null,
        Filters: QueryPlanMapper.MapFilters(plan.Filters));

    /// Gruplamasız agregasyon → tek satır, tek ölçüm. Modelin ürettiği ilk ölçüm alınır;
    /// izlenebilir bir planda zaten tek ölçüm vardır (bkz. DescribeUnwatchable).
    private static AggregateQuery ToScalarQuery(QueryPlan plan)
    {
        var full = QueryPlanMapper.ToAggregateQuery(plan);

        return full with
        {
            GroupBy = Array.Empty<string>(),
            Metrics = new[] { full.Metrics[0] },
            Bucket = null,
            Sort = null,
            Dir = null,
            Limit = null,
            // Having gruplara uygulanır; grup yoksa karşılığı da yok.
            Having = null,
            Share = false,
            SortMetric = 0,
            ShareMetric = 0
        };
    }

    /// <summary>
    /// Sonucu okur. Asıl karar burada: PostgreSQL boş kümede toplama işlemlerine NULL
    /// döndürür ve bu NULL'ın anlamı işleme göre değişir.
    ///
    ///   count / countDistinct / sum → boş kümenin cevabı SIFIRDIR. "Hiç satış yok" ile
    ///     "toplam 0" aynı şeydir; burada 0 yazmak doğru ve gereklidir, aksi hâlde
    ///     "ciro 1000'in altına düştü" izleyicisi tam da ciro sıfırlandığında susardı.
    ///
    ///   avg / min / max / median → boş kümede TANIMSIZDIR. Sıfır yazmak uydurmak olur:
    ///     "ortalama fiyat 0'a düştü" diye uyarmak, ortada hiç ürün yokken yanlış bir
    ///     olgu bildirmektir. Bu durumda değer null kalır, eşik değerlendirilmez ve
    ///     grafikte boşluk görünür.
    /// </summary>
    private static decimal? ReadValue(AggregateQuery query, IReadOnlyList<AggregateBucket> buckets)
    {
        var value = buckets.Count > 0 ? buckets[0].Value : null;
        if (value is not null) return value;

        var op = (query.Metrics[0].Op ?? "").ToLowerInvariant();
        return op is "count" or "countdistinct" or "sum" ? 0m : null;
    }
}
