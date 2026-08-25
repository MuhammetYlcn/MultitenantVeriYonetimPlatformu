using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace VeriYonetim.Api.Services;

/// <summary>
/// E-posta ayarları. Bütün alanların bir varsayılanı var ama <see cref="Host"/> yok:
/// ayarlanmamış bir sunucu adresinin makul varsayılanı olmadığı için, boş olması
/// "e-posta kapalı" demektir (bkz. <see cref="IsConfigured"/>).
/// </summary>
public class EmailOptions
{
    /// SMTP sunucusunun adresi. BOŞSA ÖZELLİK KAPALI — ve bu bilinçli: uyarı zaten
    /// veritabanında ve rozette duruyor, e-posta yalnızca onun uygulama dışına çıkan
    /// kopyası. Ayarı olmayan bir kurulumun açılışta patlaması ya da her koşuda hata
    /// basması, kolaylık olsun diye eklenmiş bir kanalın sistemi rehin alması olurdu.
    public string Host { get; set; } = string.Empty;

    /// 1025 = Mailpit'in varsayılan SMTP portu (teslimdeki yerel posta yakalayıcı).
    public int Port { get; set; } = 1025;

    /// <summary>
    /// Bağlantı güvenliği: none | starttls | ssl.
    ///
    /// Varsayılan "none", çünkü teslimdeki alıcı Mailpit ve o aynı compose ağının içinde
    /// duruyor. Gerçek bir SMTP sağlayıcısına bağlanılacaksa değer ayarla değiştirilir;
    /// kodda sabitlenmedi ki sağlayıcı değişince derleme gerekmesin.
    /// </summary>
    public string Security { get; set; } = "none";

    public string From { get; set; } = "veriyonetim@localhost";
    public string FromName { get; set; } = "Veri Yönetim";

    /// Kimlik doğrulama isteğe bağlı: Mailpit istemiyor, gerçek sağlayıcılar istiyor.
    public string? UserName { get; set; }
    public string? Password { get; set; }

    /// <summary>
    /// Gönderim için üst sınır. KISA tutuldu (10 sn) ve sebebi ölçülebilir: gönderim
    /// izleyici koşusunun içinde yapılıyor; tek taramada 200 izleyici koşabiliyor
    /// (bkz. WatchScheduler.MaxPerSweep). Cevap vermeyen bir SMTP sunucusu, uzun bir
    /// zaman aşımıyla bütün taramayı durdururdu.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// E-postadaki saatlerin yazılacağı saat dilimi.
    ///
    /// Sunucu her şeyi UTC tutuyor ama kullanıcı e-postayı kendi saatine göre okur:
    /// "14:03'te eşik aşıldı" yazan bir uyarının üç saat kaydırılmış olması, alarmın
    /// anlattığı olayı yanlış bir ana bağlar. IANA kimliği kullanılıyor çünkü teslim
    /// Linux konteynerinde yapılacak; .NET bu kimliği Windows'ta da tanıyor.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/Istanbul";

    /// Sunucu adresi verilmemişse özellik kapalıdır.
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Gönderilecek tek bir e-posta. Alıcılar liste: bir izleyici uyarısı firmanın tamamına
/// gider, kişiye değil (bkz. WatchEmailNotifier).
/// </summary>
public record EmailMessage(IReadOnlyList<string> To, string Subject, string Body);

public interface IEmailSender
{
    /// Ayar yoksa false; çağıran boşuna gövde hazırlamasın diye dışarıdan sorulabiliyor.
    bool IsEnabled { get; }

    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// SMTP üzerinden gönderim (MailKit).
///
/// System.Net.Mail.SmtpClient yerine MailKit seçildi: Microsoft kendi sınıfını yeni
/// geliştirmeler için önermiyor ve STARTTLS'i doğru konuşmuyor. Teslimde şifreli
/// bağlantı kullanılmayacak olsa da (yerel Mailpit), gerçek bir sağlayıcıya geçildiğinde
/// değişmesi gereken tek şeyin AYAR olması istendi.
///
/// HATAYI YUTMUYOR. Gönderim düşerse istisna yukarı çıkar; "uyarı kaybolmasın" kararının
/// uygulandığı yer burası değil, kanalların üstündeki CompositeWatchNotifier. Tek bir
/// yerde durması, ikinci bir kanal eklendiğinde aynı kararın tekrar yazılmasını önlüyor.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.IsConfigured;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!IsEnabled || message.To.Count == 0) return;

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.From));

        foreach (var address in message.To)
        {
            // Bozuk bir adres yüzünden BÜTÜN gönderim düşmesin: firmadaki bir kullanıcının
            // adresi elle bozulmuşsa diğerleri uyarıyı almaya devam etmeli.
            if (MailboxAddress.TryParse(address, out var mailbox))
                mime.To.Add(mailbox);
            else
                _logger.LogWarning("Geçersiz e-posta adresi atlandı: {Address}", address);
        }

        if (mime.To.Count == 0) return;

        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient
        {
            Timeout = (int)TimeSpan.FromSeconds(_options.TimeoutSeconds).TotalMilliseconds
        };

        await client.ConnectAsync(_options.Host, _options.Port, SecurityOf(_options.Security), ct);

        if (!string.IsNullOrWhiteSpace(_options.UserName))
            await client.AuthenticateAsync(_options.UserName, _options.Password ?? string.Empty, ct);

        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static SecureSocketOptions SecurityOf(string? security) => security?.ToLowerInvariant() switch
    {
        "starttls" => SecureSocketOptions.StartTls,
        "ssl" => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.None
    };
}
