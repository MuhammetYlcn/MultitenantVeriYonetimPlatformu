namespace VeriYonetim.Api.Models.Entities;

/// <summary>
/// Bir e-postaya yapılan başarısız giriş denemelerinin sayacı.
///
/// NEDEN AYRI TABLO — sayaç User/PlatformAdmin satırına konabilirdi (ASP.NET Identity
/// böyle yapar), ama o tasarım bir HESAP SAYIM (enumeration) kapısı açıyor: kayıtlı
/// olmayan bir e-postanın sayacı tutulamayacağı için, "çok fazla deneme" cevabı yalnız
/// GERÇEK hesaplarda görünürdü. Saldırgan beş yanlış şifre yollayıp cevaba bakarak
/// e-postanın sistemde olup olmadığını öğrenirdi — üstelik tam da giriş mesajlarının
/// bilinçli olarak tek tip tutulduğu bir kod tabanında (bkz. AuthService.LoginAsync).
/// Ayrı tablo, kayıtlı olmayan e-postaların da sayılmasını sağlar: kilit mesajı her iki
/// durumda da aynı şekilde çıkar, yani hiçbir şey sızdırmaz.
///
/// İkinci kazanç: tek tablo iki kimlik dünyasına birden hizmet eder (<see cref="Scope"/>),
/// yani iki ayrı varlığa iki ayrı kolon çifti eklemek ve iki ayrı bakım yazmak gerekmez.
///
/// Bu tabloda GLOBAL QUERY FILTER YOKTUR ve olamaz: kayıt, giriş denemesi sırasında —
/// yani ortada bir token, dolayısıyla bir tenant bağlamı yokken — yazılır.
/// </summary>
public class LoginAttempt
{
    public Guid Id { get; set; }

    /// Hangi giriş kapısı: <see cref="Services.LoginScopes"/> (tenant | platform).
    /// Aynı e-posta iki kapıda ayrı sayılır — biri diğerinin kilidini tetiklemesin.
    public string Scope { get; set; } = null!;

    /// KÜÇÜK HARFE indirgenmiş e-posta. Kayıt tarafı büyük/küçük harfe duyarlı
    /// olduğundan burada indirgenmezse "Ali@x.com" ile "ali@x.com" iki ayrı sayaç
    /// açar ve kilit büyük harfle yazılarak atlatılabilirdi.
    public string Email { get; set; } = null!;

    /// Pencere içindeki ardışık başarısız deneme sayısı.
    public int FailedCount { get; set; }

    public DateTime LastFailedAt { get; set; } = DateTime.UtcNow;

    /// Doluysa ve gelecekteyse giriş kapalı. Kilit KALICI DEĞİL, süreli:
    /// kalıcı kilit, e-postasını bilen herkesin bir kullanıcıyı süresiz olarak
    /// dışarıda bırakabilmesi demek olurdu (kilidin kendisi bir saldırı aracına dönerdi).
    public DateTime? LockedUntil { get; set; }
}
