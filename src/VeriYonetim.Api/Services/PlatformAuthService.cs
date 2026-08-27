using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

public interface IPlatformAuthService
{
    Task<PlatformAuthResult> LoginAsync(PlatformLoginRequest request);
    Task<PlatformAuthResult> ChangePasswordAsync(Guid adminId, PlatformChangePasswordRequest request);
    Task<bool> EnsureSeedAdminAsync();
}

/// <summary>
/// Platform yöneticisi kimlik doğrulaması. Tenant tarafındaki AuthService'ten
/// bilinçli olarak ayrı: paylaşılan tek şey imzalama anahtarıdır.
///
/// KRİTİK: platform yöneticisi oluşturmanın PUBLIC bir ucu YOKTUR. Kayıt (register)
/// self-servis olsaydı isteyen herkes tüm firmaları yönetebilirdi. Kimlik yalnızca
/// sunucu ayarlarından (appsettings / ortam değişkeni) açılışta tohumlanır — yani
/// makineye/deployment'a erişimi olan kişi belirler.
///
/// Şifrenin nerede durduğu: veritabanında YALNIZCA BCrypt hash'i durur. Ayarlardaki
/// açık şifre sadece ilk tohumlama içindir; işletmeci ilk girişten sonra
/// ChangePasswordAsync ile şifresini değiştirip ayardaki değeri silebilir — böylece
/// diskte hiçbir yerde açık şifre kalmaz.
/// </summary>
public class PlatformAuthService : IPlatformAuthService
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _config;
    private readonly ILoginThrottle _throttle;
    private readonly ILogger<PlatformAuthService> _logger;

    public PlatformAuthService(AppDbContext db, ITokenService tokenService,
        IConfiguration config, ILoginThrottle throttle, ILogger<PlatformAuthService> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _config = config;
        _throttle = throttle;
        _logger = logger;
    }

    public async Task<PlatformAuthResult> LoginAsync(PlatformLoginRequest request)
    {
        // Kaba kuvvet sınırı. Bu kapı tenant girişinden DAHA kritik: arkasında tek bir
        // hesap var, o hesabın şifresi bütün firmaların yönetimini açıyor ve adres
        // (e-posta) çoğu kurulumda tahmin edilebilir. Ayrı bir kapı olarak sayılıyor —
        // aynı e-postanın tenant tarafındaki denemeleri buranın kilidini tetiklemesin.
        var locked = await _throttle.GetLockAsync(LoginScopes.Platform, request.Email);
        if (locked is not null)
            return new PlatformAuthResult(false, LoginThrottle.LockMessage(locked.Value));

        var admin = await _db.PlatformAdmins
            .FirstOrDefaultAsync(a => a.Email == request.Email);

        // Kullanıcı yok ile şifre yanlış AYNI mesajı döner: hangi e-postanın platform
        // yöneticisi olduğu dışarıya sızmasın (hesap sayımı / enumeration).
        //
        // Mesajı eşitlemek tek başına yetmiyordu: kısa devre yüzünden kayıtsız adreste
        // BCrypt hiç çalışmıyor, kayıtlıda ~100 ms çalışıyordu. Bu kapıda farkın bedeli
        // ağır — tablo tipik olarak TEK satır taşır, yani tek bir süre ölçümü "bu adres
        // platform yöneticisidir" demeye yeter. Kullanıcı yoksa sahte karma üzerinde aynı
        // maliyet ödeniyor.
        // Doğrulama koşuldan ÖNCE ve koşulsuz çalışıyor — maliyeti eşitleyen şey bu.
        var hash = admin?.PasswordHash ?? DummyPassword.Hash;
        var passwordOk = BCrypt.Net.BCrypt.Verify(request.Password, hash);

        if (admin is null || !passwordOk)
        {
            var justLocked = await _throttle.RecordFailureAsync(
                LoginScopes.Platform, request.Email);

            return new PlatformAuthResult(false, justLocked is not null
                ? LoginThrottle.LockMessage(justLocked.Value)
                : "E-posta veya şifre hatalı.");
        }

        await _throttle.ClearAsync(LoginScopes.Platform, request.Email);

        admin.LastLoginAt = DateTime.UtcNow;

        // Platform girişi de denetim kaydına yazılır: panele kim ne zaman girdi.
        AddAudit(admin, "PlatformLogin");
        await _db.SaveChangesAsync();

        return new PlatformAuthResult(true, "Giriş başarılı.", BuildResponse(admin));
    }

    /// <summary>
    /// Platform yöneticisi kendi şifresini değiştirir. Mevcut şifre şart — çalınmış bir
    /// token'la şifrenin ele geçirilip kalıcı erişime çevrilmesini engeller.
    /// </summary>
    public async Task<PlatformAuthResult> ChangePasswordAsync(Guid adminId,
        PlatformChangePasswordRequest request)
    {
        var admin = await _db.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == adminId);
        if (admin is null)
            return new PlatformAuthResult(false, "Yönetici bulunamadı.");

        // Mevcut şifre denemeleri sayılıyor — sızmış bir platform token'ının 15 dakikalık
        // ömrü içinde şifrenin kaba kuvvetle aranıp kalıcı erişime çevrilmesini keser.
        var locked = await _throttle.GetLockAsync(LoginScopes.PasswordChange, admin.Email);
        if (locked is not null)
            return new PlatformAuthResult(false, LoginThrottle.LockMessage(locked.Value));

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, admin.PasswordHash))
        {
            var justLocked = await _throttle.RecordFailureAsync(
                LoginScopes.PasswordChange, admin.Email);

            return new PlatformAuthResult(false, justLocked is not null
                ? LoginThrottle.LockMessage(justLocked.Value)
                : "Mevcut şifre hatalı.");
        }

        await _throttle.ClearAsync(LoginScopes.PasswordChange, admin.Email);

        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, admin.PasswordHash))
            return new PlatformAuthResult(false, "Yeni şifre eskisiyle aynı olamaz.");

        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);

        AddAudit(admin, "PlatformPasswordChanged");
        await _db.SaveChangesAsync();

        // Yeni token döndürülür ki istemci oturumu kesilmeden devam edebilsin.
        // (Platform tarafında refresh token yok — iptal edilecek uzun ömürlü sır da yok.)
        return new PlatformAuthResult(true, "Şifre güncellendi.", BuildResponse(admin));
    }

    /// <summary>
    /// Açılışta çağrılır. Ayarlarda tanımlı platform yöneticisi veritabanında yoksa
    /// oluşturur. Var olanın şifresini EZMEZ — ayar dosyası eski bir şifre taşıyorsa
    /// panelden yapılan değişikliği sessizce geri almasın. true = yeni kayıt açıldı.
    /// </summary>
    public async Task<bool> EnsureSeedAdminAsync()
    {
        var email = _config["PlatformAdmin:Email"];
        var password = _config["PlatformAdmin:Password"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning("PlatformAdmin:Email/Password ayarlı değil — " +
                               "platform yöneticisi oluşturulmadı.");
            return false;
        }

        // Ayar EKSİKSE yönetici hiç oluşmuyor, yani güvenli tarafa düşülüyor. Tehlikeli
        // olan hâl doldurulmuş görünen ayar: örnek dosyadaki yer tutucu bırakılırsa
        // platformun en yetkili hesabı DEPODA YAZILI bir şifreyle açılır. Açılışı
        // durduruyoruz, çünkü bu hesabın şifresi bütün firmaların yönetimini açıyor.
        if (password.Contains("BURAYA-", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "'PlatformAdmin:Password' hâlâ örnek dosyadaki yer tutucu değeri taşıyor. " +
                "Bu şifre depoda yazılı olduğu için sır sayılmaz; gerçek bir değer verin " +
                "ya da ayarı tamamen boş bırakın (o zaman yönetici oluşturulmaz).");

        if (await _db.PlatformAdmins.AnyAsync(a => a.Email == email))
            return false;

        _db.PlatformAdmins.Add(new PlatformAdmin
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        });

        await _db.SaveChangesAsync();
        _logger.LogInformation("Platform yöneticisi oluşturuldu: {Email}", email);
        return true;
    }

    private void AddAudit(PlatformAdmin admin, string action) =>
        _db.PlatformAuditLogs.Add(new PlatformAuditLog
        {
            Id = Guid.NewGuid(),
            PlatformAdminId = admin.Id,
            PlatformAdminEmail = admin.Email,
            Action = action
        });

    private PlatformAuthResponse BuildResponse(PlatformAdmin admin) =>
        new(admin.Id, admin.Email, _tokenService.CreatePlatformAccessToken(admin));
}
