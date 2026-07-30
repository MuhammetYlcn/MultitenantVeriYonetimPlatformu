using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IAccountTokenService _accountTokens;

    public AuthController(IAuthService authService, IAccountTokenService accountTokens)
    {
        _authService = authService;
        _accountTokens = accountTokens;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var result = await _authService.RegisterAsync(request);
        if (!result.Success)
            return Conflict(new { message = result.Message });

        return Ok(result.Data);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var result = await _authService.LoginAsync(request);
        if (!result.Success)
            return Unauthorized(new { message = result.Message });

        return Ok(result.Data);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        var result = await _authService.RefreshAsync(request);
        if (!result.Success)
            return Unauthorized(new { message = result.Message });

        return Ok(result.Data);
    }

    // Kullanıcı kendi şifresini değiştirir. Hedef istekten DEĞİL token'dan gelir:
    // kimse başkasının şifresini değiştiremez. Başarılı olduğunda o kullanıcının
    // tüm refresh token'ları iptal edilir → eski oturumlar kapanır.
    [Authorize(Policy = AuthPolicies.TenantUser)]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var userId = Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : Guid.Empty;

        var result = await _accountTokens.ChangePasswordAsync(userId, request);

        return result.Success
            ? Ok(new { message = result.Message })
            : Problem(statusCode: result.StatusCode, title: result.Message);
    }

    // Tenant kullanıcısının kendi bilgisi — platform token'ı buraya da girmez
    // (tenant_id claim'i şart), yoksa tenantId'si boş anlamsız bir yanıt dönerdi.
    [Authorize(Policy = AuthPolicies.TenantUser)]
    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(new
        {
            userId = User.FindFirstValue("sub"),
            email = User.FindFirstValue("email"),
            role = User.FindFirstValue(ClaimTypes.Role),
            tenantId = User.FindFirstValue("tenant_id")
        });
    }
}
