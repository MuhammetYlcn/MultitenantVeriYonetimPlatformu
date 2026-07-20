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
