using Microsoft.AspNetCore.SignalR;
using VeriYonetim.Api.Hubs;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

/// <summary>
/// İstemciye gönderilen durum bildirimi. Sonucun KENDİSİ taşınmıyor — yalnız "bitti"
/// haberi gidiyor, tabloyu istemci ayrı bir istekle alıyor.
///
/// Sebebi: bir belgeden yüzlerce hücre çıkabiliyor ve bu yükü açık bir soket üzerinden
/// itmek, kullanıcı o sırada başka ekranda olsa bile veriyi göndermek demek. Haber küçük,
/// sonuç istendiğinde çekiliyor.
/// </summary>
public record JobStatusNotification(
    Guid JobId,
    string Kind,
    string Status,
    Guid? DatasetId,
    string? FileName,
    string? Error);

public interface IJobNotifier
{
    Task NotifyAsync(DocumentJob job, CancellationToken ct = default);
}

/// <summary>
/// Bildirimi SignalR üzerinden işi başlatan kullanıcıya gönderir.
///
/// Arayüz arkasında duruyor çünkü iş çalıştırıcısının testinde gerçek bir hub bağlantısı
/// kurmak gerekmesin — çalıştırıcının sınanacak davranışı bildirimin ağdan geçmesi değil,
/// doğru anlarda ve doğru durumla çağrılması.
/// </summary>
public class SignalRJobNotifier : IJobNotifier
{
    private readonly IHubContext<JobsHub> _hub;
    private readonly ILogger<SignalRJobNotifier> _logger;

    public SignalRJobNotifier(IHubContext<JobsHub> hub, ILogger<SignalRJobNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task NotifyAsync(DocumentJob job, CancellationToken ct = default)
    {
        var message = new JobStatusNotification(
            job.Id, job.Kind, job.Status, job.DatasetId, job.FileName, job.Error);

        try
        {
            await _hub.Clients
                .Group(JobsHub.UserGroup(job.UserId))
                .SendAsync("jobStatus", message, ct);
        }
        catch (Exception ex)
        {
            // Bildirim GÖNDERİLEMEZSE iş düşmemeli: kullanıcı bağlı olmayabilir, ağ
            // kopmuş olabilir. Sonuç zaten veritabanında duruyor ve ekran açıldığında
            // oradan okunuyor — canlı kanal bir kolaylık, tek doğruluk kaynağı değil.
            _logger.LogWarning(ex, "İş bildirimi gönderilemedi ({JobId}).", job.Id);
        }
    }
}
