using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

/// <summary>
/// Platform işletmecisinin firma (tenant) yönetimi.
///
/// KVKK SINIRI — bu dosyanın en önemli kuralı: buradaki hiçbir uç müşteri VERİSİNE
/// dokunmaz. Yalnızca metadata ve SAYI döner (kaç kullanıcı, kaç veri seti, kaç satır).
/// Veri seti adı, kolon adı, satır içeriği, kullanıcı e-postası KASITLI olarak yoktur.
/// Böylece "veri kurum dışına çıkmaz, platformu işleten bile göremez" savunması
/// sözde kalmaz, kodun şeklinden okunur. (Kanıt testleri: PlatformAdminTests.)
///
/// Çapraz-tenant erişim: normalde her sorgu global query filter ile tek firmaya
/// kilitlidir. Platform katmanı bu filtrenin TEK meşru istisnasıdır ve istisna
/// IgnoreQueryFilters ile açıkça, yalnız sayım sorgularında kullanılır.
/// </summary>
[ApiController]
[Route("api/platform")]
[Authorize(Policy = AuthPolicies.PlatformAdmin)]
public class PlatformTenantsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PlatformTenantsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Tüm firmalar + metadata sayıları.</summary>
    [HttpGet("tenants")]
    public async Task<IActionResult> GetTenants()
    {
        // IgnoreQueryFilters sorgunun TAMAMI için (alt sorgular dahil) filtreleri kapatır.
        // Tenant'ın kendi filtresi yok; buradaki amaç Users/Datasets/DatasetRows
        // sayımlarının tenant filtresine takılmaması — platform token'ında tenant_id
        // claim'i olmadığından filtre aksi hâlde her sayımı 0 döndürürdü.
        var tenants = await _db.Tenants
            .IgnoreQueryFilters()
            .OrderBy(t => t.CreatedAt)
            .Select(t => new TenantSummaryResponse(
                t.Id,
                t.Name,
                t.Slug,
                t.IsActive,
                t.CreatedAt,
                t.SuspendedAt,
                _db.Users.Count(u => u.TenantId == t.Id),
                _db.Datasets.Count(d => d.TenantId == t.Id),
                // Satır SAYISI metadata'dır; satır İÇERİĞİ hiçbir zaman okunmaz.
                _db.DatasetRows.Count(r => r.Dataset.TenantId == t.Id)))
            .ToListAsync();

        return Ok(tenants);
    }

    /// <summary>Panelin üst şeridi için platform geneli toplamlar.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var tenantCounts = await _db.Tenants
            .IgnoreQueryFilters()
            .GroupBy(t => t.IsActive)
            .Select(g => new { IsActive = g.Key, Count = g.Count() })
            .ToListAsync();

        var active = tenantCounts.FirstOrDefault(c => c.IsActive)?.Count ?? 0;
        var suspended = tenantCounts.FirstOrDefault(c => !c.IsActive)?.Count ?? 0;

        var userCount = await _db.Users.IgnoreQueryFilters().CountAsync();
        var datasetCount = await _db.Datasets.IgnoreQueryFilters().CountAsync();
        var rowCount = await _db.DatasetRows.IgnoreQueryFilters().CountAsync();

        return Ok(new PlatformStatsResponse(
            active + suspended, active, suspended, userCount, datasetCount, rowCount));
    }

    /// <summary>
    /// Firmayı askıya alır ya da yeniden etkinleştirir. Askı = giriş ve oturum
    /// yenileme reddi; VERİ SİLİNMEZ (geri alınabilir bir durumdur).
    /// </summary>
    [HttpPut("tenants/{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateTenantStatusRequest request)
    {
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant is null)
            return Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Firma bulunamadı.");

        tenant.IsActive = request.IsActive;
        tenant.SuspendedAt = request.IsActive ? null : DateTime.UtcNow;

        if (!request.IsActive)
        {
            // Askıya almanın ANINDA etkili olması için o firmanın açık oturumlarının
            // refresh token'ları da iptal edilir. Aksi hâlde kullanıcı access token'ı
            // dolduğunda sessizce yenileyip çalışmaya devam edebilirdi.
            // (Elde duran access token'lar en fazla 15 dk daha geçerli kalır — bu
            //  bilinçli bir ödünç: her istekte firma durumunu sorgulamak yerine
            //  kısa token ömrüne güveniyoruz. Rol değişikliğinde de aynı desen.)
            await _db.RefreshTokens
                .IgnoreQueryFilters()
                .Where(r => r.User.TenantId == id && r.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTime.UtcNow));
        }

        _db.PlatformAuditLogs.Add(new PlatformAuditLog
        {
            Id = Guid.NewGuid(),
            PlatformAdminId = this.PlatformAdminId() ?? Guid.Empty,
            PlatformAdminEmail = this.PlatformAdminEmail() ?? "bilinmiyor",
            Action = request.IsActive ? "TenantActivated" : "TenantSuspended",
            TargetTenantId = tenant.Id,
            TargetTenantName = tenant.Name
        });

        await _db.SaveChangesAsync();

        return Ok(new
        {
            tenant.Id,
            tenant.Name,
            tenant.IsActive,
            tenant.SuspendedAt
        });
    }

    /// <summary>Platform işlemlerinin denetim izi (en yeniden eskiye).</summary>
    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        var logs = await _db.PlatformAuditLogs
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .Select(l => new PlatformAuditLogResponse(
                l.Id, l.PlatformAdminEmail, l.Action,
                l.TargetTenantId, l.TargetTenantName, l.CreatedAt))
            .ToListAsync();

        return Ok(logs);
    }
}
