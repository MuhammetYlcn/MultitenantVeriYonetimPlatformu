using System.ComponentModel.DataAnnotations;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Models.Dtos;

// Doğal dil sorusu. Veri seti kimliği YOK: hangi setin (ya da setlerin) kullanılacağına
// model karar verir — gerçek bir müşteride onlarca set olur ve kullanıcının her seferinde
// doğru olanı seçmesini beklemek işi kullanıcıya geri yıkmak olurdu.
public record AskRequest([Required, MaxLength(500)] string Question);

// Satır listesi sonucu. Kolon adları ayrı taşınıyor çünkü JOIN'li sonuçta sütunlar
// birden çok veri setinden gelir ("Musteriler.sehir").
public record AskRowsResult(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows);

public record AskAggregateResult(
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<MetricSpec> Metrics,
    string? Bucket,
    IReadOnlyList<AggregateBucket> Buckets);

public record AskComparisonResult(
    string Period,
    string Previous,
    IReadOnlyList<ComparisonBucket> Buckets);

// Sorgu yanıtı.
//
// Sql ve Summary bilinçli olarak DAİMA döner: kullanıcı yalnız sonuca değil, sorusunun
// nasıl anlaşıldığına da bakabilsin. Modelin soruyu yanlış anladığı durumu fark etmenin
// tek yolu budur.
public record AskResponse(
    string Question,
    string Kind,                          // "rows" | "aggregate" | "unsupported"
    string Summary,                       // "anladığım sorgu"
    IReadOnlyList<string> Datasets,       // kullanılan veri setleri
    string? Reason = null,                // yalnız kind=unsupported
    string? Sql = null,
    int PlanMs = 0,
    int QueryMs = 0,
    AskRowsResult? Rows = null,
    AskAggregateResult? Aggregate = null,
    AskComparisonResult? Comparison = null);
