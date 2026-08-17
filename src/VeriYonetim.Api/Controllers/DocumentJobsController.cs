using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

/// <summary>
/// Kuyruğa alınmış belge işlerinin okunması.
///
/// Canlı kanal (SignalR) varken bu uçların neden gerektiği: bildirim bir KOLAYLIKTIR,
/// doğruluk kaynağı değil. Kullanıcı bağlantısı kopmuşken iş bitmiş olabilir, ekranı
/// yeni açmış olabilir, başka cihazdan bakıyor olabilir. Durumun tek kesin kaydı
/// veritabanındaki satırdır ve buradan okunur.
///
/// Görünürlük KİŞİSEL: kullanıcı yalnız kendi işlerini görür. Aynı firmadaki başka bir
/// Editor'ün yüklediği belge, henüz onaylanmamış ham bir okumadır — firmanın verisi
/// hâline gelmesi onaydan sonradır.
/// </summary>
[ApiController]
[Route("api/jobs")]
[Authorize(Policy = AuthPolicies.TenantUser)]
public class DocumentJobsController : ControllerBase
{
    // Listede gösterilen en fazla iş. Ekranda "son işlerim" kutusu var, arşiv yok.
    private const int MaxListed = 20;

    private readonly AppDbContext _db;

    public DocumentJobsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Kullanıcının son belge işleri — sonuç gövdesi HARİÇ.</summary>
    /// Sonuç bir belgede yüzlerce hücre olabiliyor; listede yirmi işin tamamını taşımak
    /// megabaytlarca gövde demek. Liste durumu gösterir, ayrıntı tek tek çekilir.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var jobs = await _db.DocumentJobs
            .Where(j => j.UserId == userId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(MaxListed)
            .ToListAsync(ct);

        return Ok(jobs.Select(j => DocumentJobMapper.ToResponse(j, includeResult: false)));
    }

    /// <summary>Tek bir işin durumu ve (bittiyse) sonucu.</summary>
    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> Get(Guid jobId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        // Global query filter firmayı zaten daraltıyor; UserId denetimi kişisel görünürlük
        // için. Başkasının işi 403 değil 404 döner — kaydın varlığı sızdırılmıyor.
        var job = await _db.DocumentJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);

        if (job is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "İş bulunamadı.");

        return Ok(DocumentJobMapper.ToResponse(job));
    }

    /// <summary>
    /// İşe ait belge görüntüsü — onay ekranı çıkan hücreleri belgeyle yan yana gösteriyor.
    /// </summary>
    /// Görüntü onaydan sonra silindiği için bu uç 404 dönebilir; ekran bunu bir hata
    /// olarak değil, "belge artık saklanmıyor" olarak karşılamalı.
    [HttpGet("{jobId:guid}/image")]
    public async Task<IActionResult> Image(Guid jobId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var job = await _db.DocumentJobs
            .Where(j => j.Id == jobId && j.UserId == userId)
            .Select(j => new { j.Image, j.ImageContentType })
            .FirstOrDefaultAsync(ct);

        if (job?.Image is null)
            return Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Belge görüntüsü bulunamadı.");

        return File(job.Image, job.ImageContentType ?? "image/jpeg");
    }

    /// <summary>
    /// İşi ATAR: kayıt ve saklanan görüntü silinir.
    ///
    /// Neden gerekli: yanlış bir belge yüklendiğinde kullanıcının elinde onu ortadan
    /// kaldıracak hiçbir araç yoktu; iş "kontrol bekliyor" durumunda kalıcı olarak
    /// duruyordu. Ekranı kapatmak işi bitirmez — ikisi farklı şeyler.
    ///
    /// Kaydedilmiş bir iş de silinebilir: veri setine yazılmış satırlar bundan
    /// etkilenmez, silinen yalnız okuma kaydıdır.
    /// </summary>
    [HttpDelete("{jobId:guid}")]
    public async Task<IActionResult> Delete(Guid jobId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var job = await _db.DocumentJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);

        if (job is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "İş bulunamadı.");

        // Çalışmakta olan bir iş de silinebilir: arka plan çalıştırıcısı kaydı bulamayınca
        // sessizce çıkıyor (bkz. DocumentJobRunner). Kullanıcıyı, yanlış yüklediği belgenin
        // okunmasının bitmesini beklemeye zorlamanın bir anlamı yok.
        _db.DocumentJobs.Remove(job);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : null;
}
