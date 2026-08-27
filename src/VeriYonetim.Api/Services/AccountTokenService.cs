using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

public interface IAccountTokenService
{
    Task<ServiceResult<AccountTokenResponse>> InviteAsync(Guid tenantId, Guid createdBy,
        InviteUserRequest request);

    Task<ServiceResult<AccountTokenResponse>> CreatePasswordResetAsync(Guid tenantId,
        Guid createdBy, Guid targetUserId);

    Task<ServiceResult<AccountTokenInfoResponse>> InspectAsync(string rawToken);

    Task<ServiceResult<string>> AcceptAsync(string rawToken, AcceptAccountTokenRequest request);

    Task<ServiceResult<string>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request);
}

/// <summary>Servis katmanı sonucu: başarı + mesaj + veri (AuthResult ile aynı desen).</summary>
public record ServiceResult<T>(bool Success, string Message, T? Data = default, int StatusCode = 200);

/// <summary>
/// Hesap açma (davet) ve şifre yönetimi. Tasarımın tek cümlelik özeti:
/// ŞİFREYİ YALNIZCA KULLANICININ KENDİSİ BİLİR.
///
/// Bu yüzden Admin'in şifre girdiği bir uç yoktur. Admin yalnızca tek kullanımlık
/// bir bağlantı üretir; şifre o bağlantıyı açan kişi tarafından belirlenir.
/// </summary>
public class AccountTokenService : IAccountTokenService
{
    // Davet uzun ömürlü olabilir (kişi e-postasını birkaç gün sonra görebilir).
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    // Şifre sıfırlama kısa ömürlü: elde duran bir sıfırlama bağlantısı, hesabı ele
    // geçirmenin en kolay yoludur — pencere dar tutulur.
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(2);

    public const string PurposeInvite = "Invite";
    public const string PurposeReset = "PasswordReset";

    private readonly AppDbContext _db;
    private readonly ILoginThrottle _throttle;

    public AccountTokenService(AppDbContext db, ILoginThrottle throttle)
    {
        _db = db;
        _throttle = throttle;
    }

    public async Task<ServiceResult<AccountTokenResponse>> InviteAsync(Guid tenantId,
        Guid createdBy, InviteUserRequest request)
    {
        // E-posta GLOBAL benzersiz. Global query filter kendi tenant'ıyla sınırlar;
        // IgnoreQueryFilters olmadan başka tenant'taki mükerrer e-posta görülemez ve
        // kullanıcı daveti kabul ettiği anda DB unique index hatası (500) alınırdı.
        // Karşılaştırma ve saklama normalleştirilmiş kimlik üzerinden. Duyarlı
        // karşılaştırmayla "Ali@x.com", kayıtlı "ali@x.com"u göremiyordu: yönetici
        // kişinin zaten var olduğunu bilmeden ikinci bir kimlik açıyordu.
        var email = EmailIdentity.Canonical(request.Email);

        var emailTaken = await _db.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.Email == email);
        if (emailTaken)
            return Fail<AccountTokenResponse>("Bu e-posta zaten kayıtlı.", 409);

        // Aynı e-postaya duran açık bir davet varsa onu geçersizleştir: her zaman
        // tek bir geçerli bağlantı olsun (eski bağlantı elden ele dolaşmasın).
        // Normalleştirilmiş adresle aranıyor — aksi hâlde farklı yazımdaki eski davet
        // bağlantısı açık kalırdı.
        await InvalidateOpenTokensAsync(tenantId, email, PurposeInvite);

        var raw = NewRawToken();
        var token = new AccountToken
        {
            Id = Guid.NewGuid(),
            Purpose = PurposeInvite,
            TenantId = tenantId,
            Email = email,
            Role = request.Role,
            TokenHash = Sha256(raw),
            ExpiresAt = DateTime.UtcNow.Add(InviteLifetime),
            CreatedByUserId = createdBy
        };

        _db.AccountTokens.Add(token);
        await _db.SaveChangesAsync();

        return Ok(new AccountTokenResponse(raw, token.Email, token.Role,
            token.ExpiresAt, token.Purpose), "Davet oluşturuldu.");
    }

    public async Task<ServiceResult<AccountTokenResponse>> CreatePasswordResetAsync(
        Guid tenantId, Guid createdBy, Guid targetUserId)
    {
        // Global query filter hedefi kendi tenant'ıyla sınırlar (FindAsync DEĞİL:
        // FindAsync filtreyi atlar ve çapraz-tenant sızdırırdı).
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId);
        if (user is null)
            return Fail<AccountTokenResponse>("Kullanıcı bulunamadı.", 404);

        await InvalidateOpenTokensAsync(tenantId, user.Email, PurposeReset);

        var raw = NewRawToken();
        var token = new AccountToken
        {
            Id = Guid.NewGuid(),
            Purpose = PurposeReset,
            TenantId = tenantId,
            Email = user.Email,
            UserId = user.Id,
            TokenHash = Sha256(raw),
            ExpiresAt = DateTime.UtcNow.Add(ResetLifetime),
            CreatedByUserId = createdBy
        };

        _db.AccountTokens.Add(token);
        await _db.SaveChangesAsync();

        return Ok(new AccountTokenResponse(raw, token.Email, null,
            token.ExpiresAt, token.Purpose), "Şifre sıfırlama bağlantısı oluşturuldu.");
    }

    /// <summary>
    /// Bağlantı açıldığında ekranda ne yazacağını belirler ("X firmasına Editör olarak
    /// davet edildiniz" / "Şifrenizi belirleyin"). Token'ı HARCAMAZ.
    /// </summary>
    public async Task<ServiceResult<AccountTokenInfoResponse>> InspectAsync(string rawToken)
    {
        var token = await FindUsableAsync(rawToken);
        if (token is null)
            return Fail<AccountTokenInfoResponse>(
                "Bağlantı geçersiz veya süresi dolmuş. Yöneticinizden yeni bağlantı isteyin.", 404);

        return Ok(new AccountTokenInfoResponse(token.Purpose, token.Email, token.Role,
            token.Tenant.Name, token.ExpiresAt), "Bağlantı geçerli.");
    }

    /// <summary>
    /// Kullanıcı şifresini belirler. Davette hesap oluşur, sıfırlamada şifre değişir.
    /// Token her iki durumda da harcanır (tek kullanımlık).
    /// </summary>
    public async Task<ServiceResult<string>> AcceptAsync(string rawToken,
        AcceptAccountTokenRequest request)
    {
        var token = await FindUsableAsync(rawToken);
        if (token is null)
            return Fail<string>(
                "Bağlantı geçersiz veya süresi dolmuş. Yöneticinizden yeni bağlantı isteyin.", 404);

        if (token.Purpose == PurposeInvite)
        {
            // Davet açıldıktan sonra aynı e-posta başka yolla kaydolmuş olabilir.
            var emailTaken = await _db.Users.IgnoreQueryFilters()
                .AnyAsync(u => u.Email == token.Email);
            if (emailTaken)
                return Fail<string>("Bu e-posta artık kullanımda. Yöneticinizle görüşün.", 409);

            _db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                TenantId = token.TenantId,
                Email = token.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Role = token.Role!
            });
        }
        else
        {
            var user = await _db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == token.UserId);
            if (user is null)
                return Fail<string>("Kullanıcı bulunamadı.", 404);

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

            // Şifre değişince eski oturumlar ölmeli: sıfırlamanın sebebi çoğu zaman
            // "hesabım ele geçirildi"dir, saldırganın açık oturumu devam etmemeli.
            await RevokeRefreshTokensAsync(user.Id);
        }

        token.UsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok("ok", token.Purpose == PurposeInvite
            ? "Hesabınız oluşturuldu, giriş yapabilirsiniz."
            : "Şifreniz güncellendi, giriş yapabilirsiniz.");
    }

    /// <summary>Giriş yapmış kullanıcı kendi şifresini değiştirir.</summary>
    public async Task<ServiceResult<string>> ChangePasswordAsync(Guid userId,
        ChangePasswordRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
            return Fail<string>("Kullanıcı bulunamadı.", 404);

        // Mevcut şifre denemeleri de sayılıyor. Şart tek başına yetmiyordu: sınır yokken
        // sızmış bir token'ın 15 dakikalık ömrü, mevcut şifreyi kaba kuvvetle aramaya
        // yetecek kadar denemeye izin veriyordu — bulunursa geçici erişim kalıcıya döner.
        var locked = await _throttle.GetLockAsync(LoginScopes.PasswordChange, user.Email);
        if (locked is not null)
            return Fail<string>(LoginThrottle.LockMessage(locked.Value), 429);

        // Mevcut şifre şart: çalınmış bir token'la şifrenin değiştirilip erişimin
        // kalıcı hâle getirilmesini engeller.
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            var justLocked = await _throttle.RecordFailureAsync(
                LoginScopes.PasswordChange, user.Email);

            return Fail<string>(justLocked is not null
                ? LoginThrottle.LockMessage(justLocked.Value)
                : "Mevcut şifre hatalı.", justLocked is not null ? 429 : 400);
        }

        // Doğru şifre sayacı sıfırlar: kendi şifresini bilen kullanıcı, daha önce yanlış
        // yazdığı için sonraki denemesinde duvara toslamamalı.
        await _throttle.ClearAsync(LoginScopes.PasswordChange, user.Email);

        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, user.PasswordHash))
            return Fail<string>("Yeni şifre eskisiyle aynı olamaz.", 400);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);

        // Şifre değişikliği tüm oturumları kapatır (bu cihaz dahil): aksi hâlde
        // şifresi çalınmış kullanıcı şifresini değiştirse bile saldırganın refresh
        // token'ı 7 gün daha çalışmaya devam ederdi.
        await RevokeRefreshTokensAsync(user.Id);
        await _db.SaveChangesAsync();

        return Ok("ok", "Şifreniz güncellendi. Güvenlik için yeniden giriş yapın.");
    }

    // ---- Yardımcılar ----

    /// <summary>
    /// Ham token'ı özetleyip geçerli (harcanmamış, süresi dolmamış) kaydı bulur.
    /// IgnoreQueryFilters bilinçli: bu istek GİRİŞ ÖNCESİ gelir, tenant context yoktur —
    /// hangi tenant'a ait olduğunu token'ın kendisi taşır.
    /// </summary>
    private async Task<AccountToken?> FindUsableAsync(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;

        var hash = Sha256(rawToken);
        var token = await _db.AccountTokens
            .IgnoreQueryFilters()
            .Include(t => t.Tenant)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (token is null || token.UsedAt is not null || token.ExpiresAt < DateTime.UtcNow)
            return null;

        // Firma askıya alınmışsa davet/sıfırlama da işlemez — askı her kapıyı kapatmalı.
        if (!token.Tenant.IsActive) return null;

        return token;
    }

    private async Task InvalidateOpenTokensAsync(Guid tenantId, string email, string purpose) =>
        await _db.AccountTokens
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.Email == email
                        && t.Purpose == purpose && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, DateTime.UtcNow));

    private async Task RevokeRefreshTokensAsync(Guid userId) =>
        await _db.RefreshTokens
            .IgnoreQueryFilters()
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTime.UtcNow));

    // 32 bayt kriptografik rastgelelik → tahmin edilemez. URL'de taşınacağı için
    // base64url (+/ yerine -_ , dolgu yok).
    private static string NewRawToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static ServiceResult<T> Ok<T>(T data, string message) => new(true, message, data);

    private static ServiceResult<T> Fail<T>(string message, int statusCode) =>
        new(false, message, default, statusCode);
}
