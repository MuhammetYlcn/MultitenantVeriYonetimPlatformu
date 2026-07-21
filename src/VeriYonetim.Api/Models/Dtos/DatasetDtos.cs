using System.ComponentModel.DataAnnotations;

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

// Sayfalanmış satır listesi yanıtı (toplam + sayfa metadata'sı ile).
public record RowListResponse(
    int Page,
    int PageSize,
    int Total,
    int TotalPages,
    IReadOnlyList<RowItem> Rows);

// Tek bir agregasyon grubu: anahtar (grup değeri, text), agregasyon sonucu, grup büyüklüğü.
public record AggregateBucket(string? Key, decimal? Value, int Count);

// Agregasyon yanıtı: hangi soru soruldu + grupların listesi.
public record AggregateResponse(
    string GroupBy,
    string Op,
    string? Metric,
    string? Bucket,
    IReadOnlyList<AggregateBucket> Buckets);
