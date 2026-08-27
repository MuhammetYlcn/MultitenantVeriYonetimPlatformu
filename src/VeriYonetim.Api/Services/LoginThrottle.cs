using System.Data;
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

    /// <summary>
    /// Şifre değiştirme uçlarındaki "mevcut şifre" denemeleri. Giriş kapılarından AYRI
    /// sayılıyor: buradaki başarısız denemeler kullanıcının giriş yapmasını
    /// engellememeli, ama sınırsız da kalmamalı.
    ///
    /// Sınır olmadan uç şu açığı taşıyordu: sızmış bir erişim token'ı (ör. SignalR'ın
    /// sorgu dizesindeki access_token'ın vekil sunucu günlüğüne düşmesi) 15 dakika
    /// geçerli. O süre boyunca saldırgan mevcut şifreyi sınırsız hızda deneyip bulursa
    /// şifreyi değiştirir ve GEÇİCİ erişimi KALICI erişime çevirir. Ucun mevcut şifreyi
    /// şart koşmasının tek amacı buydu; sınırsız deneme şartın değerini düşürüyordu.
    /// </summary>
    public const string PasswordChange = "pwchange";
}

/// <summary>
/// E-posta kimliğinin TEK tanımı.
///
/// Bu sınıf, iki katmanın aynı kimliği farklı tanımlamasından doğan bir kusuru kapatmak
/// için açıldı: giriş sayacı e-postayı küçük harfe indirgiyordu, kayıt/davet/giriş
/// sorguları ise PostgreSQL'in varsayılan harmanlamasıyla büyük-küçük harfe DUYARLI
/// karşılaştırıyordu. İki somut sonucu vardı:
///
///   • <c>ali@firma.com</c> kayıtlıyken <c>Ali@firma.com</c> mükerrerlik denetiminden
///     geçiyordu — aynı posta kutusuna ait İKİ ayrı hesap açılabiliyordu. Davet akışında
///     yönetici, kişinin zaten kayıtlı olduğunu göremeden ikinci bir kimlik açıyordu.
///   • Tersi yönde: <c>Ali@firma.com</c> olarak kayıtlı gerçek kullanıcı adresini küçük
///     harfle yazıp beş kez denerse, sayaç iki yazımı tek anahtarda birleştirdiği için
///     KENDİ hesabını 15 dakika kilitliyordu — hiçbir zaman doğru hesaba ulaşamadan.
///
/// Kural: e-posta HER ZAMAN normalleştirilmiş biçimde saklanır ve karşılaştırılır.
/// Böylece <c>Users.Email</c> üzerindeki düz benzersizlik indeksi, fiilen harf
/// duyarsız bir kısıt hâline gelir.
/// </summary>
public static class EmailIdentity
{
    /// <summary>Kırpar ve küçük harfe indirger. Boş ya da 320 karakterden uzunsa null.</summary>
    public static string? Normalize(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var key = email.Trim().ToLowerInvariant();
        return key.Length <= 320 ? key : null;
    }

    /// <summary>
    /// Saklama/karşılaştırma için normalleştirir; değer kullanılamazsa kırpılmış özgün
    /// metni döndürür. Çağıranın ayrıca doğrulama yaptığı yerlerde (DTO'da
    /// <c>[EmailAddress]</c>) bu yeterli.
    /// </summary>
    public static string Canonical(string email) => Normalize(email) ?? email.Trim();
}

/// <summary>
/// Kayıtsız e-postada da ödenecek sahte şifre doğrulama maliyeti.
///
/// İki giriş kapısı da <c>kullanıcı yoksa || şifre yanlışsa</c> biçiminde yazılmıştı ve
/// C# kısa devre yaptığı için kayıtsız e-postada BCrypt hiç çalışmıyordu. Mesajlar birebir
/// aynı olsa bile ~100 ms'lik bu fark ölçülebilir ve "bu adres kayıtlı mı" sorusunu
/// cevaplar; giriş sayacının kayıtsız e-postaları da sayarak kapattığı hesap sayımı kapısı
/// sürenin kendisinden yeniden açılırdı. Karma bir kez, gerçek şifrelerle AYNI iş
/// katsayısıyla üretiliyor — maliyetler ancak böyle eşitlenir.
/// </summary>
public static class DummyPassword
{
    public static readonly string Hash =
        BCrypt.Net.BCrypt.HashPassword("zaman-esitleyici-sahte-sifre");
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

        // ARTIRIM TEK BİR ATOMİK İFADEDE.
        //
        // Bu, sayacın en kritik yeri. EF ile yazıldığında oku-değiştir-yaz oluyordu ve
        // iki yarış durumuna birden açıktı: satır YOKKEN iki istek de eklemeye kalkar
        // (benzersizlik indeksi ikincisini reddeder), satır VARKEN iki istek aynı değeri
        // okuyup aynı değeri yazar (son yazan kazanır, bir artış kaybolur). İkincisinin
        // bedeli ağırdı: paralel yirmi istek yollayan bir saldırgan yirmi şifre deneyip
        // sayacı yalnız BİR artırıyordu, yani beş denemelik sınır fiilen "beş TUR"
        // sınırına dönüyor ve koruma büyüklük mertebesinde zayıflıyordu.
        //
        // `INSERT ... ON CONFLICT DO UPDATE` bunun tamamını veritabanında, satır kilidi
        // altında yapıyor: okuma ile yazma arasına başka bir işlem giremez. Kilitleme
        // kararı da aynı ifadenin içinde, çünkü ayrı bir UPDATE'e bırakılsaydı o adım
        // yeni bir yarışa açık olurdu.
        //
        // Pencere dolduysa sayaç 1'den başlar (aylar içinde birikmiş dört yanlış deneme
        // beşincide masum bir kullanıcıyı kilitlememeli). Sınıra ulaşıldığında kilit
        // kuruluyor ve sayaç sıfırlanıyor — aksi hâlde kilit bittikten sonraki İLK yanlış
        // deneme hemen yeni bir kilit açar, süreli kilit fiilen kalıcıya dönerdi.
        var now = DateTime.UtcNow;
        var staleBefore = now.AddMinutes(-_options.WindowMinutes);
        var lockUntil = now.AddMinutes(_options.LockMinutes);

        const string sql = """
            INSERT INTO "LoginAttempts"
                ("Id", "Scope", "Email", "FailedCount", "LastFailedAt", "LockedUntil")
            VALUES (@id, @scope, @email,
                    CASE WHEN 1 >= @max THEN 0 ELSE 1 END,
                    @now,
                    CASE WHEN 1 >= @max THEN @lockUntil ELSE NULL END)
            ON CONFLICT ("Scope", "Email") DO UPDATE SET
                "FailedCount" = CASE
                    WHEN (CASE
                            WHEN "LoginAttempts"."FailedCount" > 0
                             AND "LoginAttempts"."LastFailedAt" < @staleBefore THEN 1
                            ELSE "LoginAttempts"."FailedCount" + 1
                          END) >= @max THEN 0
                    ELSE (CASE
                            WHEN "LoginAttempts"."FailedCount" > 0
                             AND "LoginAttempts"."LastFailedAt" < @staleBefore THEN 1
                            ELSE "LoginAttempts"."FailedCount" + 1
                          END)
                END,
                "LastFailedAt" = @now,
                "LockedUntil" = CASE
                    WHEN (CASE
                            WHEN "LoginAttempts"."FailedCount" > 0
                             AND "LoginAttempts"."LastFailedAt" < @staleBefore THEN 1
                            ELSE "LoginAttempts"."FailedCount" + 1
                          END) >= @max THEN @lockUntil
                    ELSE "LoginAttempts"."LockedUntil"
                END
            RETURNING "FailedCount", "LockedUntil"
            """;

        var connection = _db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "id", Guid.NewGuid());
        AddParameter(command, "scope", scope);
        AddParameter(command, "email", key);
        AddParameter(command, "max", _options.MaxAttempts);
        AddParameter(command, "now", now);
        AddParameter(command, "staleBefore", staleBefore);
        AddParameter(command, "lockUntil", lockUntil);

        // Bağlantıyı açar (açıksa dokunmaz) — dosyanın geri kalanıyla aynı desen.
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync();

        int failedCount;
        DateTime? lockedUntil;

        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            failedCount = reader.GetInt32(0);
            lockedUntil = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }

        // Sayaç sıfırlanmışken dolu duran kilit, "bu deneme kilidi AÇTI" demektir.
        // Kilidi açan denemeye de kilit mesajı gösteriliyor: "hatalı şifre" deyip susmak,
        // kullanıcıyı bir sonraki denemesinde beklenmedik bir duvara toslatırdı.
        if (failedCount != 0 || lockedUntil is null) return null;

        var remaining = lockedUntil.Value - now;
        if (remaining <= TimeSpan.Zero) return null;

        // Kilit UYARI seviyesinde loglanıyor: bu, birinin şifre denediğine dair sunucuda
        // kalan tek iz. E-posta yazılıyor (kimin hesabı hedef alınmış), DENENEN ŞİFRE
        // ASLA yazılmıyor — log dosyası bir şifre deposuna dönüşmemeli.
        _logger.LogWarning(
            "Giriş kilidi: {Scope} kapısında {Email} adresi {Minutes} dakika kilitlendi " +
            "({Attempts} başarısız deneme).",
            scope, key, _options.LockMinutes, _options.MaxAttempts);

        return remaining;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
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

        // ÖLÜ REFRESH TOKEN'LAR da burada budanıyor.
        //
        // Tablo için hiçbir bakım yoktu: iptal edilmiş ve süresi dolmuş satırlar sonsuza
        // kadar birikiyordu. Güvenlik açığı değil — o satırlar zaten kullanılamıyor — ama
        // iki gerçek bedeli var: tablo ve benzersizlik indeksi durmadan büyüyor, ve olası
        // bir veritabanı sızıntısında etki alanı gereğinden geniş oluyor (hiç kimsenin
        // ihtiyaç duymadığı yıllar öncesine ait oturum özetleri).
        //
        // Yalnız ÖLÜ satırlar gidiyor: süresi dolmuş ya da iptal edilmiş. Geçerli bir
        // token'a dokunulmuyor, yani kimsenin oturumu bakım yüzünden kapanmıyor. Aynı
        // saklama süresi kullanılıyor: bu satırlar da bir olay kaydı ("bu oturum ne zaman
        // iptal edildi") ve giriş denemeleriyle aynı ömrü hak ediyor.
        var deadTokens = await _db.RefreshTokens
            .IgnoreQueryFilters()
            .Where(r => (r.RevokedAt != null && r.RevokedAt < cutoff)
                        || r.ExpiresAt < cutoff)
            .ExecuteDeleteAsync();

        if (removed > 0 || deadTokens > 0)
            _logger.LogInformation(
                "Giriş bakımı: {Removed} sayaç satırı, {Tokens} ölü oturum jetonu düşürüldü.",
                removed, deadTokens);
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
    private static string? Normalize(string? email) => EmailIdentity.Normalize(email);
}
