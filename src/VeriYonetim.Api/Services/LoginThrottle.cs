using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

/// <summary>Giriş kapıları. Aynı e-posta iki kapıda ayrı sayılır.</summary>
public static class LoginScopes
{
    public const string Tenant = "tenant";
    public const string Platform = "platform";
}

/// <summary>
/// Giriş denemesi sınırının ayarları (<c>Security:Login</c>).
///
/// Değerler koda gömülmedi çünkü doğru sayı kuruluma göre değişir: tek firmalı bir
/// kurulumda 5 deneme rahat, çağrı merkezi gibi ortak makineden girilen bir yerde dar
/// kalır. Ayar dosyasından değiştirilebilmesi, sınırın kaldırılması için kodun
/// değiştirilmesi gerekmemesi anlamına gelir.
/// </summary>
public class LoginThrottleOptions
{
    /// <summary>
    /// Kilit açılmadan önceki başarısız deneme sayısı. 0 (ya da altı) = ÖZELLİK KAPALI.
    ///
    /// 5 seçildi: BCrypt doğrulaması tek denemede ~100 ms sürüyor, yani sınırsız bırakılsa
    /// bile saniyede ~10 deneme yapılabilir — bu, sözlük saldırısı için fazlasıyla yeterli
    /// bir hız. 5 deneme + 15 dakika kilit, aynı hızı saatte 20 denemeye indiriyor.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Sınır aşılınca girişin kapalı kalacağı süre (dakika).</summary>
    public int LockMinutes { get; set; } = 15;

    /// <summary>
    /// Sayacın sıfırlanması için gereken sessizlik süresi (dakika). Bu süre boyunca
    /// yeni bir başarısız deneme gelmezse sayaç baştan başlar — aksi hâlde aylar içinde
    /// birikmiş dört yanlış deneme, beşincisinde masum bir kullanıcıyı kilitlerdi.
    /// </summary>
    public int WindowMinutes { get; set; } = 15;

    /// <summary>Bakımın el değmemiş sayaç satırlarını düşüreceği yaş (gün).</summary>
    public int RetentionDays { get; set; } = 7;

    public bool Enabled => MaxAttempts > 0;
}

public interface ILoginThrottle
{
    /// <summary>Kilit varsa kalan süre, yoksa null.</summary>
    Task<TimeSpan?> GetLockAsync(string scope, string email);

    /// <summary>Başarısız denemeyi işler. Dönen değer: bu denemeyle kilit açıldıysa kalan süre.</summary>
    Task<TimeSpan?> RecordFailureAsync(string scope, string email);

    /// <summary>Başarılı giriş: sayaç silinir.</summary>
    Task ClearAsync(string scope, string email);

    /// <summary>Bakım: kilidi geçmiş, uzun süredir dokunulmamış sayaçları düşürür.</summary>
    Task CleanAsync();
}

/// <summary>
/// Giriş denemesi sınırı — kaba kuvvetle şifre denemesini yavaşlatır.
///
/// KİLİT HESABA KONUYOR, IP'YE DEĞİL, ve bu bilinçli bir tercih:
///
///   1. Saldırgan IP değiştirebilir, kurbanın e-postasını değiştiremez. IP başına sınır,
///      korumak istediği şeyin (bir hesabın şifresi) yanındaki bir şeyi sayar.
///   2. Uygulama bir konteynerin/vekil sunucunun arkasında çalışacak. Orada bütün
///      isteklerin uzak adresi aynı görünür; IP başına sınır ya hiçbir şeyi engellemez
///      ya da ilk kaba kuvvet denemesinde BÜTÜN kullanıcıları dışarı kilitler.
///   3. Sayaç veritabanında duruyor: uygulamanın yeniden başlaması sınırı sıfırlamaz.
///      Bellekte tutulan bir sayaç, saldırganın beklemesi gereken tek şeyi bir
///      dağıtım/yeniden başlatma anına indirirdi.
///
/// KABUL EDİLEN AÇIK: bu tasarım tek hesaba yoğunlaşan saldırıyı durdurur, ÇOK SAYIDA
/// hesaba az denemeyle dağılan saldırıyı (password spraying) durdurmaz — her hesap kendi
/// beş denemesini ayrı harcar. Onun karşılığı IP/genel sınır olurdu ve yukarıdaki 2.
/// madde yüzünden bu kurulumda güvenilir biçimde kurulamıyor. Bilinen kısıt olarak
/// yazıldı, sessizce çözülmüş sayılmadı.
/// </summary>
public class LoginThrottle : ILoginThrottle
{
    private readonly AppDbContext _db;
    private readonly LoginThrottleOptions _options;
    private readonly ILogger<LoginThrottle> _logger;

    public LoginThrottle(AppDbContext db, IOptions<LoginThrottleOptions> options,
        ILogger<LoginThrottle> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TimeSpan?> GetLockAsync(string scope, string email)
    {
        if (!_options.Enabled) return null;

        var key = Normalize(email);
        if (key is null) return null;

        var row = await FindAsync(scope, key);
        return Remaining(row);
    }

    public async Task<TimeSpan?> RecordFailureAsync(string scope, string email)
    {
        if (!_options.Enabled) return null;

        var key = Normalize(email);
        if (key is null) return null;

        var row = await FindAsync(scope, key);
        var fresh = row is null;

        if (row is null)
        {
            row = new LoginAttempt { Id = Guid.NewGuid(), Scope = scope, Email = key };
            _db.LoginAttempts.Add(row);
        }

        var locked = Advance(row, scope, key);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (fresh)
        {
            // Aynı e-postaya aynı anda gelen iki başarısız deneme: ikisi de satırı
            // bulamayıp ikisi de eklemeye kalkar, benzersizlik indeksi ikincisini
            // reddeder. Sayacın burada kaybolması, sınırın paralel istek yollayarak
            // atlatılabilmesi demek olurdu — o yüzden satır yeniden okunup artırılıyor.
            // Eklenmiş ama kaydedilememiş varlık önce takipten çıkarılmalı, aksi hâlde
            // sonraki SaveChanges aynı hatayı tekrar verir.
            _db.Entry(row).State = EntityState.Detached;

            row = await FindAsync(scope, key);
            if (row is null) throw; // hata mükerrerlikten değilmiş: yutma, yukarı taşı

            locked = Advance(row, scope, key);
            await _db.SaveChangesAsync();
        }

        return locked;
    }

    /// <summary>
    /// Sayacı bir artırır ve gerekiyorsa kilidi kurar. Kayıt YAPMAZ — çağıran kaydeder,
    /// böylece mükerrerlik çakışmasından sonra aynı mantık ikinci kez uygulanabiliyor.
    /// </summary>
    private TimeSpan? Advance(LoginAttempt row, string scope, string key)
    {
        var now = DateTime.UtcNow;

        // Pencere dolduysa sayaç baştan başlar: aylar içinde birikmiş dört yanlış deneme,
        // beşincisinde masum bir kullanıcıyı kilitlememeli.
        var stale = row.FailedCount > 0
                    && row.LastFailedAt < now.AddMinutes(-_options.WindowMinutes);

        row.FailedCount = stale ? 1 : row.FailedCount + 1;
        row.LastFailedAt = now;

        if (row.FailedCount < _options.MaxAttempts) return null;

        row.LockedUntil = now.AddMinutes(_options.LockMinutes);

        // Sayaç kilitle birlikte sıfırlanır: aksi hâlde kilit bittikten sonraki İLK yanlış
        // deneme (sayaç hâlâ sınırın üstünde olduğu için) hemen yeni bir kilit açar ve
        // süreli kilit fiilen kalıcıya dönerdi.
        row.FailedCount = 0;

        // Kilit UYARI seviyesinde loglanıyor: bu, birinin şifre denediğine dair sunucuda
        // kalan tek iz. E-posta yazılıyor (kimin hesabı hedef alınmış), DENENEN ŞİFRE
        // ASLA yazılmıyor — log dosyası bir şifre deposuna dönüşmemeli.
        _logger.LogWarning(
            "Giriş kilidi: {Scope} kapısında {Email} adresi {Minutes} dakika kilitlendi " +
            "({Attempts} başarısız deneme).",
            scope, key, _options.LockMinutes, _options.MaxAttempts);

        return TimeSpan.FromMinutes(_options.LockMinutes);
    }

    public async Task ClearAsync(string scope, string email)
    {
        if (!_options.Enabled) return;

        var key = Normalize(email);
        if (key is null) return;

        await _db.LoginAttempts
            .Where(a => a.Scope == scope && a.Email == key)
            .ExecuteDeleteAsync();
    }

    public async Task CleanAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);

        // Kilidi hâlâ süren satır KORUNUYOR: bakım, süren bir kilidi düşürerek
        // saldırganın önünü açmamalı.
        var removed = await _db.LoginAttempts
            .Where(a => a.LastFailedAt < cutoff
                        && (a.LockedUntil == null || a.LockedUntil < DateTime.UtcNow))
            .ExecuteDeleteAsync();

        if (removed > 0)
            _logger.LogInformation("Giriş sayacı bakımı: {Removed} satır düşürüldü.", removed);
    }

    /// <summary>
    /// Kilitli hesabın giriş ekranında göreceği mesaj.
    ///
    /// Kalan süre AÇIKÇA yazılıyor. Bu bir bilgi sızıntısı değil, çünkü sayaç kayıtlı
    /// olmayan e-postalar için de tutuluyor (bkz. LoginAttempt): saldırgan bu mesajı
    /// var olmayan bir adreste de görebildiği için mesaj hesabın varlığına dair hiçbir
    /// şey söylemiyor. Gerçek kullanıcı ise "şifremi mi unuttum" diye dolanmak yerine
    /// ne kadar bekleyeceğini öğreniyor.
    /// </summary>
    public static string LockMessage(TimeSpan remaining)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"Çok fazla başarısız giriş denemesi. Lütfen {minutes} dakika sonra tekrar deneyin.";
    }

    /// <summary>Kilidin kalan süresi; kilit yoksa ya da geçmişse null.</summary>
    private static TimeSpan? Remaining(LoginAttempt? row)
    {
        if (row?.LockedUntil is not { } until) return null;

        var remaining = until - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private Task<LoginAttempt?> FindAsync(string scope, string email) =>
        _db.LoginAttempts.FirstOrDefaultAsync(a => a.Scope == scope && a.Email == email);

    /// <summary>
    /// Sayaç anahtarı. Boş e-posta sayılmaz (istek zaten reddedilecek), aşırı uzun
    /// e-posta da sayılmaz: kolon 320 karakterlik ve bunu aşan bir değer, sayaç
    /// tablosunu şişirmek için gönderilmiş çöpten başka bir şey olamaz.
    /// </summary>
    private static string? Normalize(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var key = email.Trim().ToLowerInvariant();
        return key.Length <= 320 ? key : null;
    }
}
