using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

/// <summary>
/// İzleyici uyarısının UYGULAMA DIŞINA çıkan kopyası.
///
/// Bu adıma kadar alarmın bütün kanalları uygulamanın içindeydi: rozet, bildirim kutusu,
/// canlı bildirim. Üçü de kullanıcının ekranın başında olmasını gerektiriyor — oysa
/// alarmın varlık sebebi tam olarak kullanıcı bakmıyorken haber vermek. E-posta, "sistem
/// kendiliğinden konuşur" iddiasının uygulamayı terk ettiği ilk yer.
///
/// ALICI FİRMANIN TAMAMI. İzleyici firmaya ait (bkz. DatasetWatch), uyarıyı yalnız kuran
/// kişiye göndermek onun izinli olduğu hafta alarmın sessiz kalması demek olurdu. Rol
/// ayrımı da yapılmıyor: Viewer aynı uyarıyı zaten uygulamada görüyor.
///
/// Adresler firma bağlamına DEĞİL, izleyicinin kendi TenantId'sine göre okunuyor. Sorgu
/// filtresine güvenilseydi, bağlamın kurulmadığı bir çağrı yolunda liste sessizce boş
/// döner ve uyarı hiç kimseye gitmezdi — izolasyon aynı, ama burada açıkça yazılı.
/// </summary>
public class WatchEmailNotifier : IWatchNotifier
{
    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly EmailOptions _options;
    private readonly ILogger<WatchEmailNotifier> _logger;

    public WatchEmailNotifier(AppDbContext db, IEmailSender email,
        IOptions<EmailOptions> options, ILogger<WatchEmailNotifier> logger)
    {
        _db = db;
        _email = email;
        _options = options.Value;
        _logger = logger;
    }

    public async Task NotifyAsync(DatasetWatch watch, DatasetWatchRun run, CancellationToken ct = default)
    {
        // Ayar yoksa hiç sorgu bile yapılmıyor: kapalı özellik veritabanına dokunmamalı.
        if (!_email.IsEnabled) return;

        var recipients = await _db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == watch.TenantId)
            .Select(u => u.Email)
            .ToListAsync(ct);

        if (recipients.Count == 0)
        {
            _logger.LogWarning("İzleyici uyarısı için adres bulunamadı: {WatchId}", watch.Id);
            return;
        }

        await _email.SendAsync(new EmailMessage(recipients, Subject(watch), Body(watch, run)), ct);

        _logger.LogInformation("İzleyici uyarısı e-postayla gönderildi: {WatchId} → {Count} adres.",
            watch.Id, recipients.Count);
    }

    // --- metin ---

    private static string Subject(DatasetWatch watch) =>
        watch.Status == WatchStatus.Broken
            ? $"İzleyici çalışmıyor: {watch.Title}"
            : $"Uyarı: {watch.Title}";

    /// <summary>
    /// Düz metin gövde. HTML yazılmadı: uyarı bir cümle ve üç satır veri, biçimlendirme
    /// eklemek okunurluğu artırmıyor — ama her istemcide aynı görünmesini zorlaştırıyor.
    ///
    /// Gövdede ÖLÇÜLEN SAYI da var. Uygulamadaki bildirim kutusunda olduğu gibi: kullanıcı
    /// uyarıya baktığı anda zaten onu merak ediyor, sırf değeri görmek için uygulamayı
    /// açtırmak kanalı yarım bırakmak olurdu.
    /// </summary>
    private string Body(DatasetWatch watch, DatasetWatchRun run)
    {
        var lines = new List<string>();

        if (run.Error is not null)
        {
            lines.Add($"\"{watch.Title}\" izleyicisi ÇALIŞMIYOR; ölçüm yapılamadı.");
            lines.Add("");
            lines.Add($"Sebep: {run.Error}");
            // Kırık izleyicinin en tehlikeli hâli sessiz kalmasıydı; ikinci tehlikesi
            // kullanıcının ne yapacağını bilmemesi. Sebep tek başına teknik kalıyor.
            lines.Add("İzlenen sorunun dayandığı veri seti ya da kolon değişmiş olabilir.");
        }
        else
        {
            lines.Add($"\"{watch.Title}\" izleyicisinin eşiği aşıldı.");
            lines.Add("");
            lines.Add($"Ölçülen değer: {Number(run.Value)}");
            lines.Add($"Kural: {Describe(watch)}");

            // DEĞİŞİM izleyicisinde karşılaştırmanın hangi ana göre yapıldığı YAZILIYOR.
            //
            // Metin sabitti ("önceki ölçüme göre") ama taban keyfî derecede eski
            // olabiliyor: duraklatma, uzun kırık dönem, firma askısı. Cuma duraklatılıp
            // Pazartesi sürdürülen saatlik bir izleyici, üç günün artışını "önceki
            // ölçüme göre" diye sunuyordu — kullanıcı bir saatlik ani sıçrama sanıp
            // olmayan bir olayı araştırıyordu.
            if (watch.ConditionKind == WatchConditionKind.Change
                && watch.PreviousValueAt is { } tabanZamani)
            {
                lines.Add($"Karşılaştırılan önceki ölçüm: {LocalTime(tabanZamani)} " +
                          $"({Number(watch.PreviousValue)})");
            }
        }

        lines.Add($"Ölçüm zamanı: {LocalTime(run.RanAt)}");
        lines.Add("");
        lines.Add($"İzlenen soru: {watch.Question}");
        lines.Add("");
        // Kapanış cümlesi, e-postanın doğruluk kaynağı OLMADIĞINI söylüyor: gönderim
        // düşse de uyarı uygulamada duruyor, kullanıcı oradan da görebiliyor.
        lines.Add("Bu uyarı uygulamadaki İzleyiciler ekranında da duruyor.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Eşik kuralının düz Türkçe okunuşu — plan özetiyle aynı yaklaşım: kullanıcıya
    /// "gt/1000" değil, ne olduğunda haber verildiği anlatılıyor.
    ///
    /// Cümleler sayıya EK ALMAYACAK biçimde kuruldu ("1.000 sınırının üzerine çıktığında",
    /// "1.000'den büyük" değil): Türkçede ek sayının okunuşuna göre değişiyor (beşten ama
    /// binden) ve rakamı çözmeden doğrusu yazılamaz. Sınır kelimesi bu sorunu ortadan
    /// kaldırıyor, üstelik cümle de kısalıyor.
    /// </summary>
    private static string Describe(DatasetWatch watch)
    {
        var limit = watch.ConditionKind == WatchConditionKind.Change
            ? $"önceki ölçüme göre değişim %{Number(watch.Threshold)}"
            : $"değer {Number(watch.Threshold)}";

        return watch.ConditionOp switch
        {
            "gt" => $"{limit} sınırının üzerine çıktığında",
            "gte" => $"{limit} sınırına ulaştığında ya da üzerine çıktığında",
            "lt" => $"{limit} sınırının altına indiğinde",
            _ => $"{limit} sınırına indiğinde ya da altına düştüğünde"
        };
    }

    private static string Number(decimal? value) =>
        value?.ToString("#,##0.##", CultureInfo.GetCultureInfo("tr-TR")) ?? "—";

    /// <summary>
    /// UTC zamanı okuyucunun saatine çevirir. Saat dilimi bulunamazsa UTC yazılır ve
    /// AÇIKÇA "(UTC)" diye etiketlenir: yanlış bir yerel saat yazmaktansa, hangi saate
    /// göre olduğu belli olan bir zaman yazmak yeğdir.
    /// </summary>
    private string LocalTime(DateTime utc)
    {
        // Dilim ARTIK AYRI BİR AYARDAN OKUNMUYOR: uygulamanın iş saat dilimi ne ise o
        // (bkz. RelativePeriod.BusinessZone, ayar `App:TimeZone`).
        //
        // İki ayarın ayrı olması sessiz bir tutarsızlık üretiyordu: dönem sınırları
        // sunucunun yerel saatiyle (konteynerde UTC), e-postadaki damga ise Email:TimeZone
        // ile hesaplanıyordu. Sonuç, "27.08 01:30" damgalı bir uyarıda 26.08'in rakamını
        // görmekti — kullanıcının fark etmesinin hiçbir yolu yoktu. Tek kaynak olunca
        // ikisi tanım gereği ayrışamıyor.
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc), RelativePeriod.BusinessZone);

        return local.ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("tr-TR"));
    }
}
