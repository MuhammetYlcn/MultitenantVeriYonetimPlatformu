namespace VeriYonetim.Api.Models.Entities;

/// <summary>
/// Platform yöneticisi: tenant'ların ÜSTÜNDE duran işletmeci kimliği. Firmaları
/// (tenant) yönetir; müşteri verisine erişmez.
///
/// Neden User tablosunda bir bayrak DEĞİL de ayrı tablo:
/// User.TenantId zorunlu (non-nullable) ve tüm User sorguları
/// "TenantId == aktif tenant" global filtresinden geçiyor. Platform yöneticisi
/// hiçbir tenant'a ait olmadığı için User'a konsa TenantId nullable olmak zorunda
/// kalır, bu da izolasyon filtresinin anlamını bozar ve her tenant sorgusunun
/// "tenant'ı olmayan kullanıcılar" durumunu ayrıca düşünmesini gerektirir.
/// Ayrı tablo sayesinde platform kimliği bir tenant'ın kullanıcı listesinde
/// yapısal olarak GÖRÜNEMEZ — izolasyon tasarımla garanti edilir.
/// </summary>
public class PlatformAdmin
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}
