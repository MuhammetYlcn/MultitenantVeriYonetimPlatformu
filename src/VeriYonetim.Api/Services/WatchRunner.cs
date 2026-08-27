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
    /// <param name="manual">
    /// Kullanıcının "şimdi kontrol et" düğmesinden geldi mi.
    ///
    /// Elle koşu bir SINAMADIR, zamanlanmış ölçüm serisinin bir üyesi değil. Ayrım
    /// gerekiyor, çünkü ayrılmadığında kullanıcının "acaba çalışıyor mu" davranışı
    /// ölçümün tanımını değiştiriyordu: elle koşu değişim tabanını (LastValue/
    /// PreviousValue) kaydırıyor ve sıradaki koşuyu ileri atıyordu. Saatlik bir
    /// izleyicide 10:59'da basılan düğme, 11:00 ölçümünü "bir saatin değişimi" olmaktan
    /// çıkarıp "bir dakikanın değişimi" hâline getiriyor ve gerçek sıçramayı yutuyordu.
    ///
    /// Elle koşu bu yüzden: ölçer, geçmişe yazar, durumu/hatayı tazeler (kırık bir
    /// izleyici düzeltildiğinde ekranda görülebilsin diye) — ama tabana, sıradaki koşu
    /// zamanına ve eşik durumuna DOKUNMAZ ve uyarı ÜRETMEZ.
    /// </param>
    Task<DatasetWatchRun> ExecuteAsync(DatasetWatch watch, bool manual = false,
        CancellationToken ct = default);
}

public class WatchRunner : IWatchRunner
{
    /// <summary>
    /// Kaç ardışık başarısız koşudan sonra izleyici "kırık" sayılır.
    ///
    /// İki seçildi, bir değil: bir koşuluk titremeler (bağlantı düşmesi, anlık kilit)
    /// gerçek bir kırılma değil ve her birine uyarı üretmek alarmı gürültüye çevirir.
    /// Yükseltilmedi de, çünkü gerçekten kırılmış bir izleyicinin sessiz kaldığı süre
    /// koşu sıklığının katı kadar uzar — "çalışmayan alarma güvenilmemesi" ilkesi
    /// gecikmenin de sınırlı olmasını gerektiriyor.
    /// </summary>
    private const int BrokenAfterFailures = 2;

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

    public async Task<DatasetWatchRun> ExecuteAsync(DatasetWatch watch, bool manual = false,
        CancellationToken ct = default)
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

            watch.ConsecutiveFailures++;

            // GEÇİCİ HATA İLE KALICI KIRILMA AYRILIYOR.
            //
            // Eskiden tek bir başarısız koşu doğrudan "kırık" demekti ve eşik durumunu da
            // sıfırlıyordu. Bir olay için üç uyarı çıkıyordu: eşik aşıldı (1) → bir koşuda
            // bağlantı titredi, kırık (2) + IsBreaching sıfırlandı → sonraki koşuda değer
            // hâlâ aynı, "yeni geçiş" sanıldı (3). Veride hiçbir şey değişmemişti.
            //
            // Eşiğin altındaki başarısızlıkta koşu kaydı yine yazılıyor (geçmişte iz
            // kalıyor) ama izleyicinin DURUMU korunuyor ve uyarı üretilmiyor.
            if (watch.ConsecutiveFailures < BrokenAfterFailures)
            {
                notify = false;
            }
            else
            {
                watch.Status = WatchStatus.Broken;
                watch.Error = error;

                // Eşik durumu sıfırlanır: izleyici düzeldiğinde değer hâlâ eşiğin
                // dışındaysa bu kullanıcı için YENİ bir haberdir — arada kırık olduğu
                // için uyarıyı görmemiştir.
                watch.IsBreaching = false;

                // Kenar tetikleme kırılmada da geçerli: saatte bir "hâlâ bozuk" demek,
                // uyarıyı gürültüye çevirir.
                notify = !wasBroken;
            }
        }
        else
        {
            var breached = Evaluate(watch, value);

            run.Value = value;
            run.Error = null;
            run.Breached = breached;

            // Ölçüm tuttu: ardışık hata sayacı sıfırlanır.
            watch.ConsecutiveFailures = 0;

            // TABAN yalnız ölçülebilen koşularda kayar.
            //
            // Eskiden `LastValue = value` koşulsuzdu ve tanımsız ölçüm (boş kümede
            // ortalama/medyan) tabanı SİLİYORDU. Bedeli değişim izleyicilerinde sessiz bir
            // körlüktü: 02:00'de o gün henüz satış yokken avg NULL çıkıyor, taban
            // kayboluyor; 03:00'te değer 400 ölçülse bile previous null olduğu için
            // karşılaştırma atlanıyor; 04:00'te 410 → %2,5. 100'den 400'e sıçrayan gerçek
            // olay hiçbir zaman değerlendirilmiyor ve izleyici "izliyor" görünmeye devam
            // ediyordu — kırık bile değil. Taban olarak son ÖLÇÜLEBİLEN değer tutuluyor;
            // koşu kaydına null yine yazılıyor, yani geçmişte boşluk görünür kalıyor.
            // Elle koşu tabana DOKUNMAZ (bkz. IWatchRunner.ExecuteAsync/manual).
            if (value is not null && !manual)
            {
                watch.PreviousValue = watch.LastValue;
                watch.PreviousValueAt = watch.LastValueAt;
                watch.LastValue = value;
                watch.LastValueAt = now;
            }

            // Eşik durumu da yalnız zamanlanmış seride ilerler: elle bir sınama,
            // zamanlanmış koşuların gördüğü "önceki durum"u değiştirmemeli.
            if (!manual) watch.IsBreaching = breached;

            watch.Status = breached ? WatchStatus.Breaching : WatchStatus.Ok;
            watch.Error = null;

            // Uyarı yalnız GEÇİŞTE: eşiğin dışında kalmaya devam etmek yeni bir olay değil.
            notify = breached && !wasBreaching;
        }

        // ELLE KOŞU HİÇ UYARI ÜRETMEZ.
        //
        // İki sebebi var. Birincisi, düğmeye basan kullanıcı sonucu zaten ekranda
        // görüyor. İkincisi ve önemlisi: RunNow duraklatılmış izleyicide de çalışıyor.
        // Susturulmuş bir izleyici, veri taşıma sırasında merakla basılan bir düğme
        // yüzünden firmadaki HERKESE "Kritik stok" e-postası gönderebiliyordu — ekip
        // susturduğuna güvendiği bir alarmdan yanlış haber alıyordu.
        if (manual) notify = false;

        run.Notified = notify;
        if (notify) watch.LastTriggeredAt = now;

        watch.LastRunAt = now;

        // Sıradaki koşu zamanı da yalnız zamanlanmış koşuda ileri atılıyor. Elle koşu
        // takvimi kaydırsaydı, "çalışıyor mu" diye bakmak izleyiciyi kendi periyodundan
        // saptırırdı — saatlik bir izleyicide 10:59'da basılan düğme sıradaki ölçümü
        // 11:59'a atıyordu.
        if (!manual) watch.NextRunAt = now.AddMinutes(watch.IntervalMinutes);

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
    /// <summary>
    /// Koşu yolundaki kısayol: önceki değer olarak <c>watch.LastValue</c> alınır. Bu yalnız
    /// koşu sırasında doğru, çünkü orada <c>Evaluate</c> LastValue güncellenmeden ÖNCE
    /// çağrılıyor. Başka her yerde önceki değer AÇIKÇA verilmeli.
    /// </summary>
    public static bool Evaluate(DatasetWatch watch, decimal? value) =>
        Evaluate(watch, value, watch.LastValue);

    /// <summary>
    /// Önceki değeri açıkça alan biçim.
    ///
    /// Ayrı bir aşırı yükleme gerekti, çünkü kod incelemesinde iki çağıranın da yanlış
    /// "önceki" ile çalıştığı bulundu ve ikisi de <c>change</c> izleyicilerinde sessiz
    /// hatalar üretiyordu:
    ///
    ///   • PATCH, durumu yeniden değerlendirirken <c>Evaluate(watch, watch.LastValue)</c>
    ///     çağırıyordu; aynı alan hem current hem previous olduğu için değişim DAİMA %0
    ///     çıkıyordu. Eşiği aşmış bir izleyicinin yalnızca ADINI değiştirmek IsBreaching'i
    ///     false yapıyor, bir sonraki koşu aynı sürmekte olan olay için İKİNCİ kez uyarı
    ///     ve e-posta üretiyordu. Kenar tetiklemenin engellemek için var olduğu şey
    ///     düzenleme düğmesiyle tetikleniyordu.
    ///   • Kurulumda <c>LastValue</c> nesne başlatıcıda yazıldığı için ilk değerlendirme
    ///     de kendisiyle karşılaştırılıyordu; sıfırın eşiği sağladığı operatörlerde
    ///     (ör. "%0 ya da altına inerse uyar") izleyici doğar doğmaz "aşıldı" işaretleniyor
    ///     ve İLK GERÇEK düşüş yutuluyordu.
    /// </summary>
    public static bool Evaluate(DatasetWatch watch, decimal? value, decimal? previousValue)
    {
        // Değer üretilemedi (ör. boş kümede ortalama). Durum korunur: ne yeni uyarı
        // doğar ne de var olan uyarı sessizce kapanır.
        if (value is not decimal current) return watch.IsBreaching;

        if (watch.ConditionKind == WatchConditionKind.Value)
            return WatchConditionOps.Matches(watch.ConditionOp, current, watch.Threshold);

        // --- değişim yüzdesi ---
        // İlk koşuda karşılaştırılacak önceki değer yok: taban kaydedilir, uyarı doğmaz.
        if (previousValue is not decimal previous) return false;

        // Sıfırdan çıkışın yüzdesi tanımsızdır (0 → 5 kaç kat artış?). Uydurulmuş bir
        // sayıyla uyarmaktansa bu koşu atlanır.
        if (previous == 0m) return false;

        var changePercent = (current - previous) / Math.Abs(previous) * 100m;

        return WatchConditionOps.Matches(watch.ConditionOp, changePercent, watch.Threshold);
    }
}
