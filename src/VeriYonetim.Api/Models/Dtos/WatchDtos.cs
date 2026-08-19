using System.ComponentModel.DataAnnotations;

namespace VeriYonetim.Api.Models.Dtos;

/// <summary>
/// İzleyici kurma isteği.
///
/// Plan İSTEMCİDEN GELMİYOR — yalnız hangi cevabın izleneceği (MessageId) geliyor.
/// Sebebi ispat edilebilirlik: plan istemciden alınsaydı, izlenen sorgunun ekranda cevabı
/// gösterilen sorguyla aynı olduğunu hiçbir şey garanti etmezdi. Sunucu planı kendi
/// kaydından okuyor (bkz. AskMessage.PlanJson).
/// </summary>
public record CreateWatchRequest(
    [Required] Guid MessageId,
    [Required] int IntervalMinutes,
    [Required] string ConditionKind,
    [Required] string Op,
    decimal Threshold,
    [MaxLength(200)] string? Title = null);

/// Değiştirilebilenler. Verilmeyen alan olduğu gibi kalır — soru ve planı DEĞİŞTİRİLEMEZ:
/// izlenen ölçüm değişirse değer geçmişi anlamını yitirir, grafikteki kırılmanın verideki
/// değişimden mi tanımın değişmesinden mi geldiği bilinemez olurdu.
public record UpdateWatchRequest(
    [MaxLength(200)] string? Title = null,
    int? IntervalMinutes = null,
    string? ConditionKind = null,
    string? Op = null,
    decimal? Threshold = null,
    bool? IsEnabled = null);

/// Listede görünen izleyici.
public record WatchSummary(
    Guid Id,
    string Title,
    string Question,
    string Status,
    bool IsEnabled,
    int IntervalMinutes,
    string ConditionKind,
    string Op,
    decimal Threshold,
    decimal? LastValue,
    decimal? PreviousValue,
    DateTime? LastRunAt,
    DateTime? LastTriggeredAt,
    DateTime NextRunAt,
    string? Error,
    string CreatedBy,
    int UnreadCount);

/// Tek bir koşu — değer geçmişi grafiğinin noktası.
public record WatchRunDto(
    Guid Id, DateTime RanAt, decimal? Value, bool Breached, string? Error, bool Notified, DateTime? ReadAt);

/// İzleyicinin ayrıntısı: özet + planın Türkçe okunuşu + koşu geçmişi (grafiğin kaynağı).
public record WatchDetail(
    WatchSummary Watch,
    string Summary,
    IReadOnlyList<WatchRunDto> Runs);

/// Okundu işaretleme. RunIds boş bırakılırsa okunmamış uyarıların TAMAMI kapatılır
/// ("tümünü temizle") — kullanıcı bildirim kutusunu tek tek boşaltmak zorunda kalmasın.
public record MarkAlertsReadRequest(IReadOnlyList<Guid>? RunIds = null);

/// Okunmamış uyarı — bildirim kutusunun satırı.
public record WatchAlertDto(
    Guid RunId,
    Guid WatchId,
    string Title,
    DateTime RanAt,
    decimal? Value,
    string? Error,
    bool Broken);
