using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request);
    Task<AuthResult> LoginAsync(LoginRequest request);
    Task<AuthResult> RefreshAsync(RefreshRequest request);
}

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _config;
    private readonly ITenantProvisioner _provisioner;

    public AuthService(AppDbContext db, ITokenService tokenService, IConfiguration config,
        ITenantProvisioner provisioner)
    {
        _db = db;
        _tokenService = tokenService;
        _config = config;
        _provisioner = provisioner;
    }

    public async Task<AuthResult> RegisterAsync(RegisterRequest request)
    {
        // E-posta global benzersiz olmalı. Kayıt sırasında tenant context yok, o yüzden
        // IgnoreQueryFilters ile tüm tenant'lar arasında kontrol edilir.
        var emailTaken = await _db.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.Email == request.Email);
        if (emailTaken)
            return new AuthResult(false, "Bu e-posta zaten kayıtlı.");

        // Slug firma adından otomatik türetilir (iç detay); benzersiz olması sağlanır.
        var slug = await MakeUniqueSlugAsync(Slugify(request.TenantName));

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.TenantName,
            Slug = slug,
            SchemaName = _provisioner.BuildSchemaName(slug)
        };

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = "Admin",
            Tenant = tenant
        };

        _db.Tenants.Add(tenant);
        _db.Users.Add(user);
        var refreshRaw = CreateRefreshToken(user);

        // Tenant kaydı ve şeması ya birlikte oluşur ya hiç oluşmaz.
        // PostgreSQL'de DDL transactional olduğundan CREATE SCHEMA da rollback edilebilir.
        await using var tx = await _db.Database.BeginTransactionAsync();
        await _provisioner.CreateSchemaAsync(tenant.SchemaName);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return new AuthResult(true, "Kayıt başarılı.", BuildResponse(user, refreshRaw));
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request)
    {
        // E-posta global benzersiz → tek başına kullanıcıyı bulmaya yeter. Login token
        // öncesi olduğundan tenant context yok; IgnoreQueryFilters ile global aranır.
        var user = await _db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email == request.Email);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return new AuthResult(false, "E-posta veya şifre hatalı.");

        var refreshRaw = CreateRefreshToken(user);
        await _db.SaveChangesAsync();

        return new AuthResult(true, "Giriş başarılı.", BuildResponse(user, refreshRaw));
    }

    public async Task<AuthResult> RefreshAsync(RefreshRequest request)
    {
        var hash = Sha256(request.RefreshToken);

        var stored = await _db.RefreshTokens
            .IgnoreQueryFilters()
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.TokenHash == hash);

        if (stored is null)
            return new AuthResult(false, "Geçersiz refresh token.");

        if (stored.RevokedAt is not null)
        {
            // İptal edilmiş token'ın tekrar kullanılması = çalınma şüphesi.
            // Bu kullanıcının tüm aktif oturumları kapatılır.
            await _db.RefreshTokens
                .IgnoreQueryFilters()
                .Where(r => r.UserId == stored.UserId && r.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTime.UtcNow));

            return new AuthResult(false, "Geçersiz refresh token.");
        }

        if (stored.ExpiresAt < DateTime.UtcNow)
            return new AuthResult(false, "Refresh token süresi dolmuş, yeniden giriş yapın.");

        // Rotation: eski token iptal, yeni çift üretilir.
        stored.RevokedAt = DateTime.UtcNow;
        var refreshRaw = CreateRefreshToken(stored.User);
        await _db.SaveChangesAsync();

        return new AuthResult(true, "Token yenilendi.", BuildResponse(stored.User, refreshRaw));
    }

    // Firma adını BuildSchemaName'in beklediği güvenli slug formatına indirger
    // (^[a-z][a-z0-9-]*$): Türkçe karakterler ASCII'ye, boşluk/ayraç tireye, gerisi atılır.
    private static string Slugify(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);

        foreach (var ch in lower)
        {
            var c = ch switch
            {
                'ş' => 's', 'ğ' => 'g', 'ı' => 'i', 'ü' => 'u', 'ö' => 'o', 'ç' => 'c',
                'â' => 'a', 'î' => 'i', 'û' => 'u',
                _ => ch
            };

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (c is ' ' or '-' or '_' or '.')
                sb.Append('-');
            // diğer tüm karakterler (noktalama, injection denemeleri vb.) atılır
        }

        // ardışık tireleri sadeleştir, baş/sondaki tireleri temizle
        var slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');

        // şema kuralı: harfle başlamalı; boşsa veya rakamla başlıyorsa "t" öneki ekle
        if (slug.Length == 0 || !char.IsLetter(slug[0]))
            slug = "t" + slug;

        // BuildSchemaName 56 sınırı — güvenli pay bırak, sondaki tireyi tekrar temizle
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');

        return slug;
    }

    // Türetilen slug başka bir tenant'ta varsa sonuna -2, -3… ekleyerek benzersizleştirir.
    private async Task<string> MakeUniqueSlugAsync(string baseSlug)
    {
        var slug = baseSlug;
        var suffix = 2;
        while (await _db.Tenants.AnyAsync(t => t.Slug == slug))
            slug = $"{baseSlug}-{suffix++}";
        return slug;
    }

    private string CreateRefreshToken(User user)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Sha256(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(int.Parse(_config["Jwt:RefreshTokenDays"]!)),
            User = user
        });

        return raw;
    }

    private AuthResponse BuildResponse(User user, string refreshRaw) =>
        new(user.Id, user.TenantId, user.Email, user.Role,
            _tokenService.CreateAccessToken(user), refreshRaw);

    private static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
