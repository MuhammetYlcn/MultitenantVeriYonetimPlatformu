using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

public interface ITokenService
{
    string CreateAccessToken(User user);
    string CreatePlatformAccessToken(PlatformAdmin admin);
}

public class TokenService : ITokenService
{
    private readonly IConfiguration _config;

    public TokenService(IConfiguration config)
    {
        _config = config;
    }

    public string CreateAccessToken(User user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new(AuthPolicies.TenantIdClaim, user.TenantId.ToString())
        };

        return Write(claims, int.Parse(_config["Jwt:AccessTokenMinutes"]!));
    }

    /// <summary>
    /// Platform yöneticisi token'ı. Tenant token'ından iki kritik farkı var:
    ///   • tenant_id claim'i YOK  → tenant uçlarındaki TenantUser politikası bunu eler,
    ///     ayrıca global query filter'a besleyeceği bir firma kimliği hiç oluşmaz.
    ///   • platform_admin=true    → yalnız /api/platform/* uçlarını açar.
    ///
    /// Refresh token verilmez (bilinçli): en yetkili kimliğe en kısa tasma. Süre
    /// dolunca yeniden giriş gerekir; uzun ömürlü bir platform refresh token'ı
    /// çalınırsa tüm firmaların yönetimi risk altına girerdi.
    /// </summary>
    public string CreatePlatformAccessToken(PlatformAdmin admin)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, admin.Email),
            new(ClaimTypes.Role, "PlatformAdmin"),
            new(AuthPolicies.PlatformAdminClaim, "true")
        };

        // Ayarlanmamışsa 60 dk (panel oturumu access token'dan uzun ama sınırlı).
        var minutes = int.TryParse(_config["Jwt:PlatformTokenMinutes"], out var m) ? m : 60;
        return Write(claims, minutes);
    }

    // İki token tipi aynı imza/issuer/audience ayarlarını paylaşır; yalnız claim
    // kümesi ve ömür değişir.
    private string Write(IEnumerable<Claim> claims, int minutes)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
