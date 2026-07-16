using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest request);
    Task<AuthResult> LoginAsync(LoginRequest request);
}

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;

    public AuthService(AppDbContext db, ITokenService tokenService)
    {
        _db = db;
        _tokenService = tokenService;
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
            Slug = request.TenantSlug
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
        await _db.SaveChangesAsync();

        return new AuthResult(true, "Kayıt başarılı.",
            new AuthResponse(user.Id, tenant.Id, user.Email, user.Role,
                _tokenService.CreateAccessToken(user)));
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request)
    {
        var user = await _db.Users
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Tenant.Slug == request.TenantSlug
                                   && u.Email == request.Email);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return new AuthResult(false, "E-posta veya şifre hatalı.");

        return new AuthResult(true, "Giriş başarılı.",
            new AuthResponse(user.Id, user.TenantId, user.Email, user.Role,
                _tokenService.CreateAccessToken(user)));
    }
}
