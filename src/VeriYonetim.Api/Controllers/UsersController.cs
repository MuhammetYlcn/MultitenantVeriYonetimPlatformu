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
[Authorize]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenantContext;

    public UsersController(AppDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
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

    // Sadece Admin yeni kullanıcı ekleyebilir. TenantId istekten değil token'dan
    // gelir — kullanıcı hangi tenant'a ekleneceğini seçemez.
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateUser(CreateUserRequest request)
    {
        // E-posta GLOBAL benzersiz (unique index). Global query filter sorguyu kendi
        // tenant'ıyla sınırlar; IgnoreQueryFilters olmadan başka bir tenant'taki mükerrer
        // e-posta görülemez ve 409 yerine DB unique index hatası (500) alınırdı.
        var emailTaken = await _db.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.Email == request.Email);
        if (emailTaken)
            return Conflict(new { message = "Bu e-posta zaten kayıtlı." });

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.TenantId!.Value,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = request.Role
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetUsers), new { user.Id, user.Email, user.Role });
    }

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

        // NOT: rol değişikliği mevcut access token'a yansımaz; kullanıcı bir sonraki
        // token yenilemesinde (en geç 15 dk) yeni yetkilerle çalışır.
        return Ok(new { user.Id, user.Email, user.Role, user.TenantId });
    }
}
