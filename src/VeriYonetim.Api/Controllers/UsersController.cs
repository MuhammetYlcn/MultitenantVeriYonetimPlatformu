using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

[ApiController]
[Route("api/users")]
// Politika, düz [Authorize]'ın yerini alır: token'da tenant_id claim'i ŞART.
// Platform yöneticisinin token'ında bu claim yoktur → 403. Aksi hâlde platform
// token'ı buraya girer ve global query filter'a tenant_id gelmediği için sorgu
// sessizce boş liste dönerdi; hatayı gizlemek yerine açıkça reddediyoruz.
[Authorize(Policy = AuthPolicies.TenantUser)]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IAccountTokenService _accountTokens;

    private readonly ITokenGuard _tokenGuard;

    public UsersController(AppDbContext db, ITenantContext tenantContext,
        IAccountTokenService accountTokens, ITokenGuard tokenGuard)
    {
        _db = db;
        _tenantContext = tenantContext;
        _accountTokens = accountTokens;
        _tokenGuard = tokenGuard;
    }

    // Dikkat: hiçbir Where/tenant kontrolü yok — izolasyonu global query filter sağlıyor.
    [HttpGet]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _db.Users
            .OrderBy(u => u.CreatedAt)
            .Select(u => new { u.Id, u.Email, u.Role, u.TenantId, u.CreatedAt })
            .ToListAsync();

        return Ok(users);
    }

    // Yalnız Admin kullanıcı DAVET eder. Şifre alanı bilinçli olarak YOKTUR:
    // Admin bir davet bağlantısı üretir, şifreyi kullanıcı kendisi belirler.
    // (Önceki sürümde Admin başkasının şifresini giriyordu — o akış "şifreyi yalnız
    //  sahibi bilir" ilkesini bozduğu için tamamen kaldırıldı.)
    // TenantId istekten değil token'dan gelir: davetin hangi firmaya olduğu seçilemez.
    [HttpPost("invite")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> InviteUser(InviteUserRequest request)
    {
        var result = await _accountTokens.InviteAsync(
            _tenantContext.TenantId!.Value, CurrentUserId(), request);

        return result.Success
            ? Ok(result.Data)
            : Problem(statusCode: result.StatusCode, title: result.Message);
    }

    // Admin, bir kullanıcı için tek kullanımlık şifre sıfırlama bağlantısı üretir.
    // Admin yeni şifreyi GÖRMEZ — yalnızca bağlantıyı iletir.
    [HttpPost("{id:guid}/reset-password")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreatePasswordReset(Guid id)
    {
        var result = await _accountTokens.CreatePasswordResetAsync(
            _tenantContext.TenantId!.Value, CurrentUserId(), id);

        return result.Success
            ? Ok(result.Data)
            : Problem(statusCode: result.StatusCode, title: result.Message);
    }

    private Guid CurrentUserId() =>
        Guid.TryParse(User.FindFirst("sub")?.Value, out var id) ? id : Guid.Empty;

    // Var olan bir kullanıcının rolünü değiştirir. Yalnız Admin çağırabilir; hedef
    // kullanıcı kendi tenant'ından olmak zorunda — bunu global query filter sağlıyor
    // (FindAsync DEĞİL FirstOrDefaultAsync: FindAsync filtreyi atlar, çapraz-tenant sızardı).
    [HttpPut("{id:guid}/role")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateUserRole(Guid id, UpdateUserRoleRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
            return Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Kullanıcı bulunamadı.");

        // Son-Admin koruması: bir tenant'ta her zaman en az bir Admin kalmalı.
        // Aksi hâlde kullanıcı yönetimi kilitlenir (kimse rol/kullanıcı ekleyemez).
        if (user.Role == "Admin" && request.Role != "Admin")
        {
            var adminCount = await _db.Users.CountAsync(u => u.Role == "Admin");
            if (adminCount <= 1)
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Firmadaki son yöneticinin rolü düşürülemez. Önce başka bir yönetici atayın.");
        }

        user.Role = request.Role;
        await _db.SaveChangesAsync();

        // ROL DEĞİŞİKLİĞİ ANINDA ETKİLİ.
        //
        // Buradaki eski not "mevcut access token'a yansımaz, en geç 15 dk" diyordu.
        // Yetki YÜKSELTMENİN gecikmesi bir kolaylık sorunu, ama DÜŞÜRMENİN gecikmesi
        // güvenlik sorunuydu: Admin'den Viewer'a indirilen kullanıcı, token'ındaki eski
        // rol claim'iyle 15 dakika daha yazmaya devam edebiliyordu.
        //
        // TokenGuard token'daki rolü kayıtlı rolle karşılaştırıyor; bu satır snapshot'ı
        // düşürerek karşılaştırmanın taze veriyle yapılmasını sağlıyor. Eşleşmeyince token
        // reddediliyor ve kullanıcı yenilemeye zorlanıyor — yenileme yeni rolü taşır.
        _tokenGuard.Invalidate(user.Id);

        return Ok(new { user.Id, user.Email, user.Role, user.TenantId });
    }
}
