using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Services;

public interface IWatchRunCleaner
{
    Task CleanAsync();
}

/// <summary>
/// İzleyici koşu geçmişinin bakımı.
///
/// Kapatılan açık şu: her koşu bir satır bırakıyor ve hiçbiri silinmiyordu. Saatlik koşan
/// tek bir izleyici yılda ~8.760, on beş dakikada bir koşan biri ~35.000 satır demek —
/// üstelik firma sayısıyla çarpılarak. Kimsenin bakmadığı, ama sürekli büyüyen bir tablo.
///
/// SINIR ZAMANA DEĞİL SAYIYA KONDU, ve bunun sebebi ölçülebilir: birikme hızını kullanıcı
/// seçiyor (koşu sıklığı). Zaman ölçütü ("90 günden eskisi gitsin") aynı kuralı iki
/// izleyiciye taban tabana zıt uygularadı — günlük koşan izleyicide 90 nokta bırakır,
/// on beş dakikalıkta 8.640. Sayı ölçütü sıklık ne olursa olsun aynı tavanı koyuyor.
///
/// İKİ ŞEY BİLİNÇLİ OLARAK SİLİNMİYOR:
///   1. OKUNMAMIŞ UYARILAR. Bildirim kutusu ve rozet bunları veritabanından sayıyor
///      (bkz. WatchesController.Alerts); bakımın onları düşürmesi, kullanıcının hiç
///      görmediği bir alarmı sessizce yok etmek olurdu — projenin baştan beri kovaladığı
///      "haber vermeden susma" hâlinin bakım eliyle yapılmışı. Kenar tetikleme sayesinde
///      bunların sayısı zaten küçük kalıyor: uyarı yalnız durum değişince doğuyor.
///   2. EN YENİ <see cref="KeepPerWatch"/> KOŞU. Değer geçmişi grafiği son 60 koşuyu
///      çiziyor; tavan onun çok üstünde tutuldu ki bakım, kullanıcının ekranda gördüğü
///      grafiği hiçbir koşulda kırpmasın.
///
/// Belge işlerinin bakımıyla (bkz. DocumentJobCleaner) aynı desen: firma ayrımı YAPMAZ,
/// çünkü bakım bütün kiracılar için aynı anda çalışır. Hiçbir veri okunmuyor; yalnız
/// eskimiş ölçüm noktaları düşürülüyor.
/// </summary>
public class WatchRunCleaner : IWatchRunCleaner
{
    /// İzleyici başına saklanan en fazla koşu sayısı.
    private const int KeepPerWatch = 500;

    private readonly AppDbContext _db;
    private readonly ILogger<WatchRunCleaner> _logger;

    public WatchRunCleaner(AppDbContext db, ILogger<WatchRunCleaner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task CleanAsync()
    {
        // Önce SADECE tavanı aşan izleyiciler bulunuyor. Bütün izleyicilerde tek tek
        // silme denemek, hiçbir şeyin silinmeyeceği yüzlerce sorgu demek olurdu; normal
        // hâlde bu liste boş döner ve bakım tek sorguyla biter.
        var crowded = await _db.DatasetWatchRuns
            .IgnoreQueryFilters()
            .GroupBy(r => r.WatchId)
            .Where(g => g.Count() > KeepPerWatch)
            .Select(g => g.Key)
            .ToListAsync();

        if (crowded.Count == 0) return;

        var removed = 0;

        foreach (var watchId in crowded)
        {
            // Tavandaki koşunun zamanı: bundan ESKİ olanlar fazlalık. Sınır zaman
            // üzerinden konuyor çünkü silme tek sorguda yapılacak; kimlik listesi
            // taşımak binlerce satırı belleğe getirirdi.
            var cutoff = await _db.DatasetWatchRuns
                .IgnoreQueryFilters()
                .Where(r => r.WatchId == watchId)
                .OrderByDescending(r => r.RanAt)
                .Skip(KeepPerWatch - 1)
                .Select(r => r.RanAt)
                .FirstOrDefaultAsync();

            if (cutoff == default) continue;

            // Eşitler KORUNUYOR (< yerine <= değil): aynı ana denk gelen koşular yüzünden
            // tavanın altına inilmesindense birkaç satır fazla tutulur.
            removed += await _db.DatasetWatchRuns
                .IgnoreQueryFilters()
                .Where(r => r.WatchId == watchId
                            && r.RanAt < cutoff
                            && !(r.Notified && r.ReadAt == null))
                .ExecuteDeleteAsync();
        }

        if (removed == 0) return;

        _logger.LogInformation(
            "İzleyici geçmişi bakımı: {Watches} izleyicide {Removed} koşu kaydı düşürüldü.",
            crowded.Count, removed);
    }
}
