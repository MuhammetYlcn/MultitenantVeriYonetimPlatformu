using Hangfire;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Services;

public interface IWatchScheduler
{
    /// <summary>
    /// Süresi gelmiş izleyicileri çalıştırır. Hangfire bunu düzenli aralıklarla çağırır.
    /// </summary>
    /// Yeniden deneme KAPALI: tarama zaten birkaç dakikada bir tekrar geliyor. Düşen bir
    /// taramayı Hangfire'ın ayrıca denemesi, aynı izleyicileri üst üste koşturup değer
    /// geçmişine sahte noktalar eklerdi.
    [AutomaticRetry(Attempts = 0)]
    Task SweepAsync();
}

/// <summary>
/// İzleyicilerin zamanlayıcısı — özelliği "kaydedilmiş sorgu"dan "kendiliğinden çalışan
/// alarm"a çeviren parça.
///
/// TEK BİR TARAMA, izleyici başına Hangfire kaydı DEĞİL. Her izleyici için ayrı bir
/// tekrarlayan iş açmak akla yakın görünüyor ama üç yerde kırılıyor: kayıtların uçlarla
/// birlikte oluşturulup silinmesi gerekir (yarıda kalan bir istek, veritabanında olmayan
/// bir izleyiciyi tetikleyen bir kuyruk kaydı bırakır), Hangfire kayıtları tenant kavramı
/// tanımaz, ve sıklığı değişen bir izleyici için kaydın da güncellenmesi gerekir. Tarama
/// modelinde tek doğruluk kaynağı veritabanıdır: NextRunAt neyse o çalışır.
/// </summary>
public class WatchScheduler : IWatchScheduler
{
    /// Bir taramada çalıştırılacak en fazla izleyici sayısı.
    ///
    /// Sınır olmasa, uzun süre kapalı kalmış bir sunucu açıldığında birikmiş bütün
    /// izleyiciler aynı anda koşardı. Sıraya NextRunAt'e göre girildiği için en çok
    /// gecikmiş olan önce çalışır; kalanlar bir sonraki taramaya kalır.
    private const int MaxPerSweep = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WatchScheduler> _logger;

    public WatchScheduler(IServiceScopeFactory scopeFactory, ILogger<WatchScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task SweepAsync()
    {
        var due = await FindDueAsync();
        if (due.Count == 0) return;

        var ran = 0;

        foreach (var (watchId, tenantId) in due)
        {
            // HER İZLEYİCİ İÇİN AYRI KAPSAM. Tenant bağlamı bir kapsamda yalnız bir kez
            // kurulabiliyor (bkz. TenantContext.SetForBackgroundWork) ve bu bilinçli bir
            // kısıt: tek bir kapsamın iki firmaya birden hizmet etmesi, izolasyonun
            // dayandığı tek değerin iş ortasında değişmesi demek olurdu.
            using var scope = _scopeFactory.CreateScope();

            try
            {
                scope.ServiceProvider.GetRequiredService<ITenantContextSetter>()
                    .SetForBackgroundWork(tenantId);

                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Bağlam kurulduktan SONRA okunuyor, yani bu sorgu artık query filter'dan
                // geçiyor: kayıt gerçekten o firmanınsa gelir, değilse gelmez.
                var watch = await db.DatasetWatches.FirstOrDefaultAsync(w => w.Id == watchId);

                // Tarama ile koşu arasında silinmiş olabilir.
                if (watch is null) continue;

                await scope.ServiceProvider.GetRequiredService<IWatchRunner>().ExecuteAsync(watch);
                ran++;
            }
            catch (Exception ex)
            {
                // Bir izleyicinin düşmesi TARAMAYI durdurmaz: diğerleri çalışmaya devam
                // etmeli. Koşunun kendi içindeki hatalar zaten "kırık" olarak
                // işaretleniyor (bkz. WatchRunner); buraya ancak kapsam kurulumu gibi
                // beklenmedik bir sorun düşer.
                _logger.LogError(ex, "İzleyici çalıştırılamadı: {WatchId}", watchId);
            }
        }

        _logger.LogInformation("İzleyici taraması: {Ran}/{Due} izleyici çalıştırıldı.",
            ran, due.Count);
    }

    /// <summary>
    /// Süresi gelmiş izleyiciler. Bu sorgu FİLTRESİZ, çünkü tarama bütün firmalar için
    /// aynı anda çalışıyor ve daha hiçbir firma bağlamı kurulmuş değil — yumurta-tavuk
    /// zinciri burada kırılıyor (belge işinde olduğu gibi).
    ///
    /// Hiçbir veri OKUNMUYOR: yalnız hangi izleyicinin hangi firmaya ait olduğu. Asıl
    /// okuma, bağlam kurulduktan sonra filtreli sorguyla yapılıyor.
    /// </summary>
    private async Task<List<(Guid WatchId, Guid TenantId)>> FindDueAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;

        return await db.DatasetWatches
            .IgnoreQueryFilters()
            // Askıya alınmış firmanın izleyicisi koşmaz: hesabı kapalıyken sistemin o
            // firmaya uyarı üretmeye devam etmesi tuhaf olurdu.
            .Where(w => w.IsEnabled && w.NextRunAt <= now && w.Tenant.IsActive)
            .OrderBy(w => w.NextRunAt)
            .Take(MaxPerSweep)
            .Select(w => new ValueTuple<Guid, Guid>(w.Id, w.TenantId))
            .ToListAsync();
    }
}
