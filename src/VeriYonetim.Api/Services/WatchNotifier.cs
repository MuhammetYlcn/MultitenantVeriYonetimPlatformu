using Microsoft.AspNetCore.SignalR;
using VeriYonetim.Api.Hubs;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

/// <summary>
/// İzleyici uyarısının canlı bildirimi.
///
/// Belge bildiriminden (bkz. JobStatusNotification) tek farkı ALICISI: iş bildirimi onu
/// başlatan KİŞİYE gider, izleyici uyarısı FİRMANIN TAMAMINA. Sebebi izleyicinin firmaya
/// ait olması — kuran kişi izinliyken uyarının kimseye ulaşmaması, alarmın en çok işe
/// yarayacağı anda susması olurdu.
///
/// Ölçülen değer bildirimde TAŞINIYOR (iş bildiriminin aksine): tek bir sayı, ve kullanıcı
/// bildirime bakarken zaten onu merak ediyor. Belge işinde taşınmayan şey yüzlerce hücreydi.
/// </summary>
public record WatchAlertNotification(
    Guid WatchId,
    Guid RunId,
    string Title,
    string Status,
    decimal? Value,
    string? Error,
    DateTime RanAt);

public interface IWatchNotifier
{
    Task NotifyAsync(DatasetWatch watch, DatasetWatchRun run, CancellationToken ct = default);
}

/// <summary>
/// Uyarının bütün kanalları — canlı bildirim ve e-posta — tek bir çağrının arkasında.
///
/// İki işi var ve ikincisi asıl sebebi:
///   1. Koşuyu yürüten kod (WatchRunner) kaç kanal olduğunu bilmiyor. E-posta eklenirken
///      koşu mantığına tek satır dokunulmadı; kanal eklemek bir KAYIT işi hâline geldi.
///   2. BİR KANALIN DÜŞMESİ DİĞERLERİNİ DURDURMUYOR. E-posta gönderimi istisna fırlatıyor
///      (bkz. SmtpEmailSender) ve o istisna burada yakalanıyor: uyarı zaten veritabanına
///      yazılmış durumda, kanal yalnızca onun kopyasını taşıyor. Gönderilemeyen bir posta
///      yüzünden koşunun düşmesi, izleyiciyi "kırık" işaretler ve kullanıcıya verideki bir
///      sorunu haber verirdi — oysa sorun postadaydı.
/// </summary>
public class CompositeWatchNotifier : IWatchNotifier
{
    private readonly IReadOnlyList<IWatchNotifier> _channels;
    private readonly ILogger<CompositeWatchNotifier> _logger;

    public CompositeWatchNotifier(IReadOnlyList<IWatchNotifier> channels,
        ILogger<CompositeWatchNotifier> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    public async Task NotifyAsync(DatasetWatch watch, DatasetWatchRun run,
        CancellationToken ct = default)
    {
        foreach (var channel in _channels)
        {
            try
            {
                await channel.NotifyAsync(watch, run, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Uyarı kanalı düştü ({Channel}), uyarı kaydı duruyor: {WatchId}",
                    channel.GetType().Name, watch.Id);
            }
        }
    }
}

public class SignalRWatchNotifier : IWatchNotifier
{
    private readonly IHubContext<JobsHub> _hub;
    private readonly ILogger<SignalRWatchNotifier> _logger;

    public SignalRWatchNotifier(IHubContext<JobsHub> hub, ILogger<SignalRWatchNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task NotifyAsync(DatasetWatch watch, DatasetWatchRun run, CancellationToken ct = default)
    {
        var message = new WatchAlertNotification(
            watch.Id, run.Id, watch.Title, watch.Status, run.Value, run.Error, run.RanAt);

        try
        {
            await _hub.Clients
                .Group(JobsHub.TenantGroup(watch.TenantId))
                .SendAsync("watchAlert", message, ct);
        }
        catch (Exception ex)
        {
            // Bildirim gidemezse uyarı KAYBOLMAZ: koşu kaydı Notified olarak işaretli
            // duruyor ve okunmamış rozeti onu veritabanından sayıyor. Canlı kanal bir
            // kolaylık, doğruluk kaynağı değil — belge akışında verilen kararın aynısı.
            _logger.LogWarning(ex, "İzleyici uyarısı gönderilemedi ({WatchId}).", watch.Id);
        }
    }
}
