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

    /// <summary>
    /// Bu süredir "çalışıyor" görünen iş, çalışmıyor sayılır.
    ///
    /// Bir saatten 30 dakikaya indirildi. Ölçülen en kötü hâl 3 deneme × 300 saniye
    /// (~15 dk) olduğu için bir saat gereğinden uzun bir sessizlikti: kullanıcı çalışmayan
    /// bir işi bir saat boyunca bekliyordu.
    /// </summary>
    private static readonly TimeSpan RunningTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Bu süredir "sırada" bekleyen iş, kuyruğa hiç girememiş sayılır.
    ///
    /// İş kaydı ile Hangfire kuyruğu AYRI iki işlemde yazılıyor (önce kayıt, sonra
    /// Enqueue). Sıralama bilinçli — tersi, kaydı henüz görünmeyen bir işi çalıştırma
    /// yarışı açardı — ama bedeli, Enqueue düşerse kaydın "sırada" asılı kalması.
    /// Tek işçi ve uzun süren belgeler yüzünden gerçek kuyruk beklemesi uzun olabildiği
    /// için sınır geniş tutuldu.
    /// </summary>
    private static readonly TimeSpan QueuedTimeout = TimeSpan.FromHours(2);

    /// Kaydın tamamen silinme süresi.
    private static readonly TimeSpan RecordRetention = TimeSpan.FromDays(30);

    private const string StuckMessage = "İşlem yarıda kaldı; belgeyi yeniden yükleyin.";

    private readonly AppDbContext _db;
    private readonly IJobNotifier _notifier;
    private readonly ILogger<DocumentJobCleaner> _logger;

    public DocumentJobCleaner(AppDbContext db, IJobNotifier notifier,
        ILogger<DocumentJobCleaner> logger)
    {
        _db = db;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task CleanAsync()
    {
        var now = DateTime.UtcNow;

        // 1 — asılı kalmış işleri kapat.
        //
        // İki durum birden taranıyor. `running`: iş başladı ama süreç ortasında düştü.
        // `queued`: iş kaydı yazıldı ama Hangfire kuyruğuna hiç giremedi (kayıt ile kuyruk
        // AYRI iki işlemde yazılıyor — bağlantı koparsa ya da süreç tam o an kapanırsa
        // arada kalır). Eskiden yalnız `running` taranıyordu ve StartedAt null olduğu için
        // kuyruğa girememiş iş "asılı" bile sayılmıyordu: kullanıcı ekranda sonu gelmeyen
        // bir "sırada" görüyor, kayıt 30 gün sonra sessizce siliniyordu.
        var stuckBefore = now - RunningTimeout;
        var queuedBefore = now - QueuedTimeout;

        // Görüntü baytları BELLEĞE ÇEKİLMİYOR: yalnız bildirim için gereken alanlar
        // seçiliyor. Eskiden tam varlıklar yükleniyordu ve `Image` aynı tabloda duran,
        // belge başına birkaç MB'lık bir JPEG — yoğun bir firmada iki günde biriken 300
        // onaylanmamış belge, bakım işinin tek sorguda ~900 MB'ı sürecin belleğine
        // çekmesi demekti. Aynı makinede model de çalıştığı için bu, API'yi duraklatan
        // ya da düşüren bir yol açıyordu (ve düşen süreç yeni asılı işler bırakıyordu).
        var stuck = await _db.DocumentJobs
            .IgnoreQueryFilters()
            .Where(j =>
                (j.Status == DocumentJobStatus.Running && j.StartedAt < stuckBefore)
                || (j.Status == DocumentJobStatus.Queued && j.CreatedAt < queuedBefore))
            .Select(j => new { j.Id, j.UserId, j.Kind, j.DatasetId, j.FileName })
            .ToListAsync();

        if (stuck.Count > 0)
        {
            var ids = stuck.Select(s => s.Id).ToList();

            await _db.DocumentJobs
                .IgnoreQueryFilters()
                .Where(j => ids.Contains(j.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, DocumentJobStatus.Failed)
                    .SetProperty(j => j.Error, StuckMessage)
                    .SetProperty(j => j.CompletedAt, now));

            // KULLANICIYA HABER VERİLİYOR.
            //
            // Durumu Running'den Failed'a çeviren ikinci yer burasıydı ve buradan hiçbir
            // bildirim gitmiyordu. Sonucu: sunucu (ya da modeli barındıran makine) işin
            // ortasında yeniden başlatılıyor, kayıt asılı kalıyor, bir saat sonra bakım
            // onu kapatıyor — ama ekrandaki kart SONSUZA KADAR "okunuyor" olarak duruyordu.
            // Kullanıcı ancak sayfayı yenilerse öğreniyor, öğrendiğinde bir saat kaybetmiş
            // oluyordu.
            foreach (var job in stuck)
                await _notifier.NotifyAsync(new DocumentJob
                {
                    Id = job.Id,
                    UserId = job.UserId,
                    Kind = job.Kind,
                    DatasetId = job.DatasetId,
                    FileName = job.FileName,
                    Status = DocumentJobStatus.Failed,
                    Error = StuckMessage
                });
        }

        // 2 — bitmiş ama onaylanmamış işlerin görüntüsünü düşür.
        var imageBefore = now - ImageRetention;
        var stale = await _db.DocumentJobs
            .IgnoreQueryFilters()
            .Where(j => j.Image != null && j.CompletedAt != null && j.CompletedAt < imageBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Image, (byte[]?)null));

        // 3 — eski kayıtları sil.
        var recordBefore = now - RecordRetention;
        var expired = await _db.DocumentJobs
            .IgnoreQueryFilters()
            .Where(j => j.CreatedAt < recordBefore)
            .ExecuteDeleteAsync();

        if (stuck.Count == 0 && stale == 0 && expired == 0) return;

        _logger.LogInformation(
            "Belge işi bakımı: {Stuck} asılı iş kapatıldı, {Stale} görüntü silindi, " +
            "{Expired} kayıt düşürüldü.", stuck.Count, stale, expired);
    }
}
