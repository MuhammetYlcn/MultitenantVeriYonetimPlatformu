using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

/// <summary>
/// Bir izleyiciyi bir kez çalıştırır: ölçer, eşiği değerlendirir, durumu günceller,
/// gerekiyorsa haber verir.
///
/// Tenant bağlamını KURMAZ — çağıranın kurmuş olması beklenir. İki çağıranı var ve
/// bağlamları farklı yerden geliyor: kullanıcının "şimdi kontrol et" isteğinde bağlam
/// JWT'den gelir, zamanlanmış koşuda ise iş kaydından elle kurulur (bkz. WatchScheduler).
/// Bu sınıf ikisini de bilmediği için ikisinde de aynı davranır.
/// </summary>
public interface IWatchRunner
{
    Task<DatasetWatchRun> ExecuteAsync(DatasetWatch watch, CancellationToken ct = default);
}

public class WatchRunner : IWatchRunner
{
    private readonly AppDbContext _db;
    private readonly IWatchEvaluator _evaluator;
    private readonly IWatchNotifier _notifier;
    private readonly ILogger<WatchRunner> _logger;

    public WatchRunner(AppDbContext db, IWatchEvaluator evaluator,
        IWatchNotifier notifier, ILogger<WatchRunner> logger)
    {
        _db = db;
        _evaluator = evaluator;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<DatasetWatchRun> ExecuteAsync(DatasetWatch watch, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var run = new DatasetWatchRun { Id = Guid.NewGuid(), WatchId = watch.Id, RanAt = now };

        var wasBroken = watch.Status == WatchStatus.Broken;
        var wasBreaching = watch.IsBreaching;

        decimal? value = null;
        string? error = null;

        try
        {
            var plan = QueryPlanJson.Parse(watch.PlanJson)
                ?? throw new InvalidQueryException("İzleyicinin planı okunamadı.");

            value = (await _evaluator.MeasureAsync(plan, ct)).Value;
        }
        catch (InvalidQueryException ex)
        {
            // Beklenen kırılma: kolon silinmiş, veri seti adı değişmiş, şema bozulmuş.
            error = ex.Message;
        }
        catch (Exception ex)
        {
            // Beklenmeyen hata da SESSİZ KALMAZ. İç ayrıntı kullanıcıya gösterilmiyor,
            // ama izleyici "çalışıyor" görünmeye devam etmiyor — bu adımın bütün amacı
            // çalışmayan bir alarma güvenilmemesi.
            _logger.LogError(ex, "İzleyici koşusu beklenmedik biçimde düştü: {WatchId}", watch.Id);
            error = "İzleyici çalıştırılamadı.";
        }

        bool notify;

        if (error is not null)
        {
            run.Value = null;   // "ölçemedik" — SIFIR DEĞİL (bkz. DatasetWatchRun.Value)
            run.Error = error;
            run.Breached = false;

            watch.Status = WatchStatus.Broken;
            watch.Error = error;
            // Eşik durumu sıfırlanır: izleyici düzeldiğinde değer hâlâ eşiğin dışındaysa
            // bu kullanıcı için YENİ bir haberdir — arada kırık olduğu için uyarıyı
            // görmemiştir.
            watch.IsBreaching = false;

            // Kenar tetikleme kırılmada da geçerli: saatte bir "hâlâ bozuk" demek,
            // uyarıyı gürültüye çevirir.
            notify = !wasBroken;
        }
        else
        {
            var breached = Evaluate(watch, value);

            run.Value = value;
            run.Error = null;
            run.Breached = breached;

            watch.PreviousValue = watch.LastValue;
            watch.LastValue = value;
            watch.IsBreaching = breached;
            watch.Status = breached ? WatchStatus.Breaching : WatchStatus.Ok;
            watch.Error = null;

            // Uyarı yalnız GEÇİŞTE: eşiğin dışında kalmaya devam etmek yeni bir olay değil.
            notify = breached && !wasBreaching;
        }

        run.Notified = notify;
        if (notify) watch.LastTriggeredAt = now;

        watch.LastRunAt = now;
        watch.NextRunAt = now.AddMinutes(watch.IntervalMinutes);

        _db.DatasetWatchRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        // Bildirim KAYITTAN SONRA: haber gidip de kaydın düşmesi, kullanıcının tıkladığında
        // hiçbir şey bulamadığı bir uyarı bırakırdı.
        if (notify) await _notifier.NotifyAsync(watch, run, ct);

        return run;
    }

    /// <summary>
    /// Eşik değerlendirmesi. İki koşul türü var ve ikisi de "ölçemedik" durumunda
    /// bilinçli olarak SUSUYOR — değeri olmayan bir ölçümü eşikle karşılaştırmak,
    /// olmayan bir olguyu bildirmek olurdu.
    /// </summary>
    public static bool Evaluate(DatasetWatch watch, decimal? value)
    {
        // Değer üretilemedi (ör. boş kümede ortalama). Durum korunur: ne yeni uyarı
        // doğar ne de var olan uyarı sessizce kapanır.
        if (value is not decimal current) return watch.IsBreaching;

        if (watch.ConditionKind == WatchConditionKind.Value)
            return WatchConditionOps.Matches(watch.ConditionOp, current, watch.Threshold);

        // --- değişim yüzdesi ---
        // İlk koşuda karşılaştırılacak önceki değer yok: taban kaydedilir, uyarı doğmaz.
        if (watch.LastValue is not decimal previous) return false;

        // Sıfırdan çıkışın yüzdesi tanımsızdır (0 → 5 kaç kat artış?). Uydurulmuş bir
        // sayıyla uyarmaktansa bu koşu atlanır.
        if (previous == 0m) return false;

        var changePercent = (current - previous) / Math.Abs(previous) * 100m;

        return WatchConditionOps.Matches(watch.ConditionOp, changePercent, watch.Threshold);
    }
}
