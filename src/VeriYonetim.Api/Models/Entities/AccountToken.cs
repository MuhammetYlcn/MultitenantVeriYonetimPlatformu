namespace VeriYonetim.Api.Models.Entities;

/// <summary>
/// Tek kullanımlık, süreli hesap token'ı. İki amaca hizmet eder:
///
///   • Invite        — Admin bir e-posta + rol için davet açar; kullanıcı şifresini
///                     KENDİ belirleyerek hesabı oluşturur.
///   • PasswordReset — Var olan bir kullanıcının şifresini sıfırlaması için.
///
/// Tek tabloda tutulmalarının sebebi: doğrulama kuralları birebir aynı (hash'lenmiş
/// karşılaştırma, süre kontrolü, tek kullanım). İki ayrı tablo aynı mantığı iki kez
/// yazdırırdı.
///
/// TEMEL İLKE: şifreyi yalnızca kullanıcının kendisi bilir. Admin ne davet ederken
/// ne de sıfırlarken bir şifre girer veya görür — elinde yalnızca tek kullanımlık
/// bir bağlantı olur.
///
/// Ham token veritabanında DURMAZ; RefreshToken'daki desenle aynı şekilde yalnızca
/// SHA-256 özeti saklanır. Veritabanı sızsa bile kayıtlardan çalışan bir davet
/// bağlantısı üretilemez.
/// </summary>
public class AccountToken
{
    public Guid Id { get; set; }

    /// <summary>"Invite" | "PasswordReset"</summary>
    public string Purpose { get; set; } = null!;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>Hedef e-posta (her iki amaçta da dolu).</summary>
    public string Email { get; set; } = null!;

    /// <summary>Davette verilecek rol; şifre sıfırlamada null.</summary>
    public string? Role { get; set; }

    /// <summary>Şifre sıfırlamada hedef kullanıcı; davette (henüz yok) null.</summary>
    public Guid? UserId { get; set; }

    public string TokenHash { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Dolu ise token harcanmıştır — ikinci kez kullanılamaz.</summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>İşlemi başlatan Admin (denetim için).</summary>
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
