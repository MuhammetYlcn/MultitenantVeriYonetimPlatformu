using Microsoft.AspNetCore.Mvc;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

/// <summary>
/// Davet ve şifre sıfırlama bağlantılarının karşılandığı uçlar.
///
/// Bilinçli olarak KİMLİK DOĞRULAMASIZ: davet edilen kişinin henüz hesabı yoktur,
/// şifresini unutan kişi de giriş yapamaz. Erişimi token'ın kendisi yetkilendirir —
/// tahmin edilemez (32 bayt rastgele), tek kullanımlık, süreli ve veritabanında
/// yalnızca özeti duruyor.
/// </summary>
[ApiController]
[Route("api/invitations")]
public class InvitationsController : ControllerBase
{
    private readonly IAccountTokenService _accountTokens;

    public InvitationsController(IAccountTokenService accountTokens)
    {
        _accountTokens = accountTokens;
    }

    /// <summary>
    /// Bağlantının geçerliliğini ve ne için olduğunu söyler; token'ı harcamaz.
    /// Ekranın "X firmasına Editör olarak davet edildiniz" yazabilmesi için.
    /// </summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Inspect(string token)
    {
        var result = await _accountTokens.InspectAsync(token);

        return result.Success
            ? Ok(result.Data)
            : Problem(statusCode: result.StatusCode, title: result.Message);
    }

    /// <summary>
    /// Kullanıcı şifresini belirler: davette hesap oluşur, sıfırlamada şifre değişir.
    /// Token bu adımda harcanır.
    /// </summary>
    [HttpPost("{token}/accept")]
    public async Task<IActionResult> Accept(string token, AcceptAccountTokenRequest request)
    {
        var result = await _accountTokens.AcceptAsync(token, request);

        return result.Success
            ? Ok(new { message = result.Message })
            : Problem(statusCode: result.StatusCode, title: result.Message);
    }
}
