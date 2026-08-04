using System.ComponentModel.DataAnnotations;
using VeriYonetim.Api.Services;   // MetricSpec — agregasyon yanıtı ölçümleri yankılar

namespace VeriYonetim.Api.Models.Dtos;

// İstemciden gelen — yeni veri seti oluşturma. TenantId burada YOK: token'dan gelir.
public record CreateDatasetRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description);

// İstemciden gelen — mevcut veri setini güncelleme.
public record UpdateDatasetRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description);

// İstemciye giden — entity'i sızdırmadan kontrollü yanıt.
public record DatasetResponse(
    Guid Id,
    string Name,
    string? Description,
    int RowCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

// Tek bir satır: kimlik + JSONB değerleri.
public record RowItem(Guid Id, Dictionary<string, object?> Data);

// İki veri seti arasında ilişki tanımlama isteği.
public record CreateRelationRequest(
    Guid FromDatasetId,
    [Required, MaxLength(200)] string FromColumn,
    Guid ToDatasetId,
    [Required, MaxLength(200)] string ToColumn);

// İlişki yanıtı — set adları da dönüyor ki istemci ayrıca sorgu atmasın.
public record RelationResponse(
    Guid Id,
    Guid FromDatasetId, string FromDatasetName, string FromColumn,
    Guid ToDatasetId, string ToDatasetName, string ToColumn);

// Tek satır ekleme isteği: kolon adı → değer (metin gelir; tip dönüşümü sunucuda şemaya göre).
public record AddRowRequest(Dictionary<string, string?> Values);

// Sayfalanmış satır listesi yanıtı (toplam + sayfa metadata'sı ile).
public record RowListResponse(
    int Page,
    int PageSize,
    int Total,
    int TotalPages,
    IReadOnlyList<RowItem> Rows);

// Tek bir agregasyon grubu: gruplama anahtarları, ölçüm sonuçları, grup büyüklüğü.
//
// Keys/Values liste çünkü artık çoklu gruplama ("şehir VE kategoriye göre") ve çoklu ölçüm
// ("toplam, ortalama ve adet birlikte") destekleniyor. Key/Value kısayolları tek gruplama +
// tek ölçüm bekleyen mevcut pano istemcisini kırmamak için duruyor: JSON'da her ikisi de
// yer alır, istemci hangisini okuyacağını kendi seçer.
public record AggregateBucket(
    IReadOnlyList<string?> Keys,
    IReadOnlyList<decimal?> Values,
    int Count,
    decimal? Share = null)   // grubun toplam içindeki yüzdesi; yalnız istenirse hesaplanır
{
    public string? Key => Keys.Count > 0 ? Keys[0] : null;
    public decimal? Value => Values.Count > 0 ? Values[0] : null;
}

// Agregasyon yanıtı: hangi soru soruldu + grupların listesi.
public record AggregateResponse(
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<MetricSpec> Metrics,
    string? Bucket,
    IReadOnlyList<AggregateBucket> Buckets)
{
    // Tek ölçümlü eski yanıt şeklinin karşılığı (bkz. AggregateBucket.Key/Value).
    public string? Op => Metrics.Count > 0 ? Metrics[0].Op : null;
    public string? Metric => Metrics.Count > 0 ? Metrics[0].Column : null;
}
