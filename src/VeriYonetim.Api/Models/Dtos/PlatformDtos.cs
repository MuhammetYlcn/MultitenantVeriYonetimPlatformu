using System.ComponentModel.DataAnnotations;

namespace VeriYonetim.Api.Models.Dtos;

// Platform yöneticisi girişi. Tenant girişinden AYRI uç (/api/platform/auth/login):
// platform kimliği ayrı tabloda durur ve ürettiği token'da tenant_id claim'i olmaz.
public record PlatformLoginRequest(
    [Required(ErrorMessage = "E-posta gerekli.")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin.")]
    string Email,
    [Required(ErrorMessage = "Şifre gerekli.")]
    string Password);

// Platform yöneticisi kendi şifresini değiştirir. Ayarlardaki (env) tohum şifresinin
// kalıcı olarak diskte durmaması için ilk girişten sonra kullanılması beklenir.
public record PlatformChangePasswordRequest(
    [Required(ErrorMessage = "Mevcut şifre gerekli.")]
    string CurrentPassword,
    [Required(ErrorMessage = "Yeni şifre gerekli.")]
    [MinLength(8, ErrorMessage = "Yeni şifre en az 8 karakter olmalı.")]
    string NewPassword);

public record PlatformAuthResponse(Guid AdminId, string Email, string Token);

public record PlatformAuthResult(bool Success, string Message, PlatformAuthResponse? Data = null);

// Bir firmanın platform görünümü. DİKKAT: yalnızca metadata ve SAYILAR var —
// veri seti adı, kolon adı, satır içeriği ya da kullanıcı e-postası YOK.
// "Veri kurum dışına çıkmaz, platform işletmecisi bile göremez" sınırı burada çizilir.
public record TenantSummaryResponse(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? SuspendedAt,
    int UserCount,
    int DatasetCount,
    int RowCount);

// Platform genel özeti (üst şeritteki sayaçlar).
public record PlatformStatsResponse(
    int TenantCount,
    int ActiveTenantCount,
    int SuspendedTenantCount,
    int UserCount,
    int DatasetCount,
    int RowCount);

// Firmayı askıya alma / yeniden etkinleştirme.
public record UpdateTenantStatusRequest(
    [Required(ErrorMessage = "Durum gerekli.")]
    bool IsActive);

// Denetim kaydı satırı (platform işlemlerinin izi).
public record PlatformAuditLogResponse(
    Guid Id,
    string PlatformAdminEmail,
    string Action,
    Guid? TargetTenantId,
    string? TargetTenantName,
    DateTime CreatedAt);
