using System.Security.Cryptography;
using System.Text;
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
        var slugTaken = await _db.Tenants.AnyAsync(t => t.Slug == request.TenantSlug);
        if (slugTaken)
            return new AuthResult(false, "Bu tenant adresi (slug) zaten kullanımda.");

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.TenantName,
            Slug = request.TenantSlug,
            SchemaName = _provisioner.BuildSchemaName(request.TenantSlug)
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
        var user = await _db.Users
            .IgnoreQueryFilters()
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Tenant.Slug == request.TenantSlug
                                   && u.Email == request.Email);

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
