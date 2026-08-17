using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

public interface IDocumentJobCleaner
{
    Task CleanAsync();
}

/// <summary>
/// Belge işlerinin bakımı — asenkron akışın arkasını toplayan iş.
///
/// Üç şeyi düzeltiyor ve üçü de zamanla birikip soruna dönüşecek türden:
///   1. ONAYLANMAMIŞ GÖRÜNTÜLER. Onaylanan belgenin görüntüsü kaydetme anında siliniyor
///      (bkz. DocumentsController.Confirm), ama kullanıcı sonucu görüp vazgeçerse
///      görüntü sahipsiz kalır. Süresi dolunca düşürülüyor.
///   2. ASILI KALMIŞ İŞLER. Sunucu bir işin ortasında yeniden başlatılırsa kayıt sonsuza
///      kadar "çalışıyor" görünür ve kullanıcı bitmeyecek bir işi bekler. Belge okuma en
///      kötü hâlde dakikalar sürdüğü için, saatlerdir çalışan bir iş çalışmıyor demektir.
///   3. ESKİ KAYITLAR. Tamamlanmış işler sonsuza kadar durmamalı; ekranda zaten yalnız
///      son işler görünüyor.
///
/// Bu iş firma ayrımı YAPMAZ ve yapmamalı: bakım bütün kiracılar için aynı anda çalışır.
/// Bu yüzden sorgular bilinçli olarak filtresiz — ama hiçbir veri OKUNMUYOR, yalnız
/// eskimiş kayıtlar düşürülüyor.
/// </summary>
public class DocumentJobCleaner : IDocumentJobCleaner
{
    /// Onaylanmamış belgenin görüntüsü bu süre sonunda silinir. Kullanıcı sonucu ertesi
    /// gün açıp bakabilsin diye kısa tutulmadı; kalıcı arşiv olmasın diye de uzun değil.
    private static readonly TimeSpan ImageRetention = TimeSpan.FromDays(2);

    /// Bu süredir "çalışıyor" görünen iş, çalışmıyor sayılır.
    private static readonly TimeSpan RunningTimeout = TimeSpan.FromHours(1);

    /// Kaydın tamamen silinme süresi.
    private static readonly TimeSpan RecordRetention = TimeSpan.FromDays(30);

    private readonly AppDbContext _db;
    private readonly ILogger<DocumentJobCleaner> _logger;

    public DocumentJobCleaner(AppDbContext db, ILogger<DocumentJobCleaner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task CleanAsync()
    {
        var now = DateTime.UtcNow;

        // 1 — asılı kalmış işleri kapat.
        var stuckBefore = now - RunningTimeout;
        var stuck = await _db.DocumentJobs
            .IgnoreQueryFilters()
            .Where(j => j.Status == DocumentJobStatus.Running && j.StartedAt < stuckBefore)
            .ToListAsync();

        foreach (var job in stuck)
        {
            job.Status = DocumentJobStatus.Failed;
            job.Error = "İşlem yarıda kaldı; belgeyi yeniden yükleyin.";
            job.CompletedAt = now;
        }

        // 2 — bitmiş ama onaylanmamış işlerin görüntüsünü düşür.
        var imageBefore = now - ImageRetention;
        var stale = await _db.DocumentJobs
            .IgnoreQueryFilters()
            .Where(j => j.Image != null && j.CompletedAt != null && j.CompletedAt < imageBefore)
            .ToListAsync();

        foreach (var job in stale)
            job.Image = null;

        // 3 — eski kayıtları sil.
        var recordBefore = now - RecordRetention;
        var expired = await _db.DocumentJobs
            .IgnoreQueryFilters()
            .Where(j => j.CreatedAt < recordBefore)
            .ToListAsync();

        _db.DocumentJobs.RemoveRange(expired);

        if (stuck.Count == 0 && stale.Count == 0 && expired.Count == 0) return;

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Belge işi bakımı: {Stuck} asılı iş kapatıldı, {Stale} görüntü silindi, " +
            "{Expired} kayıt düşürüldü.", stuck.Count, stale.Count, expired.Count);
    }
}
