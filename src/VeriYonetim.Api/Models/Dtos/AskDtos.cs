using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Models.Dtos;

// Doğal dil sorusu. Veri seti kimliği YOK: hangi setin (ya da setlerin) kullanılacağına
// model karar verir — gerçek bir müşteride onlarca set olur ve kullanıcının her seferinde
// doğru olanı seçmesini beklemek işi kullanıcıya geri yıkmak olurdu.
// Model boş bırakılırsa sunucunun varsayılanı kullanılır. Kullanıcı seçtiğinde ise
// yalnızca kurulu modeller kabul edilir (bkz. QueryPlannerService).
// ConversationId verilirse yanıt o sohbete eklenir; verilmezse yeni sohbet açılır.
public record AskRequest(
    [Required, MaxLength(500)] string Question,
    string? Model = null,
    Guid? ConversationId = null);

// Sohbet listesi satırı.
public record ConversationSummary(Guid Id, string Title, DateTime UpdatedAt, int MessageCount);

// Tek bir tur: soru + o soruya verilmiş yanıtın tamamı.
//
// Response ham JSON olarak taşınıyor: yanıtın şekli soruya göre değişiyor (tek değer,
// tablo, grafik, karşılaştırma) ve geçmiş kayıt VERİLDİĞİ HÂLİYLE gösterilmeli —
// yeniden hesaplanarak değil, çünkü veri o günden beri değişmiş olabilir.
public record ConversationTurn(string Question, JsonElement Response, DateTime CreatedAt);

public record ConversationDetail(
    Guid Id, string Title, IReadOnlyList<ConversationTurn> Turns);

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
    string Model = "",                    // yanıtı hangi model üretti
    Guid? ConversationId = null,          // yanıtın kaydedildiği sohbet
    string? Reason = null,                // yalnız kind=unsupported
    string? Sql = null,
    int PlanMs = 0,
    int QueryMs = 0,
    AskRowsResult? Rows = null,
    AskAggregateResult? Aggregate = null,
    AskComparisonResult? Comparison = null);
