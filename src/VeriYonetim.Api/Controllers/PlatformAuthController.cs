using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

/// <summary>
/// Platform yöneticisi girişi — tenant girişinden (/api/auth) tamamen ayrı uç.
/// Bilinçli olarak register YOK: platform kimliği yalnızca sunucu ayarlarından
/// tohumlanır (bkz. PlatformAuthService.EnsureSeedAdminAsync).
/// </summary>
[ApiController]
[Route("api/platform/auth")]
public class PlatformAuthController : ControllerBase
{
    private readonly IPlatformAuthService _platformAuth;

    public PlatformAuthController(IPlatformAuthService platformAuth)
    {
        _platformAuth = platformAuth;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(PlatformLoginRequest request)
    {
        var result = await _platformAuth.LoginAsync(request);
        if (!result.Success)
            return Unauthorized(new { message = result.Message });

        return Ok(result.Data);
    }

    [Authorize(Policy = AuthPolicies.PlatformAdmin)]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(PlatformChangePasswordRequest request)
    {
        // Hedef kullanıcı istekten DEĞİL token'dan gelir: bir platform yöneticisi
        // başka bir yöneticinin şifresini değiştiremez.
        var adminId = this.PlatformAdminId();
        if (adminId is null)
            return Unauthorized(new { message = "Geçersiz platform oturumu." });

        var result = await _platformAuth.ChangePasswordAsync(adminId.Value, request);
        if (!result.Success)
            return BadRequest(new { message = result.Message });

        return Ok(result.Data);
    }

    [Authorize(Policy = AuthPolicies.PlatformAdmin)]
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        adminId = User.FindFirst("sub")?.Value,
        email = User.FindFirst("email")?.Value,
        isPlatformAdmin = true
    });
}
