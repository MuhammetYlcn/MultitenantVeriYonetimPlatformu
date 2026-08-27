using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

/// <summary>
/// Giriş denemesi sınırı testleri. Sınanan iddia: bir hesabın şifresi sınırsız
/// denenemez, ve bu sınır hangi e-postanın kayıtlı olduğunu ELE VERMEDEN uygulanır.
/// </summary>
public class LoginThrottleTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    /// appsettings.json'daki MaxAttempts ile aynı. Testin ayardan okumaması bilinçli:
    /// ayar sessizce değişirse test bunu fark etmeli.
    private const int MaxAttempts = 5;

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public LoginThrottleTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Yardımcılar ----

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record MessageDto(string Message);

    private async Task RegisterTenantAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = name, email, password });
        response.EnsureSuccessStatusCode();
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        _client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private Task<HttpResponseMessage> PlatformLoginAsync(string email, string password) =>
        _client.PostAsJsonAsync("/api/platform/auth/login", new { email, password });

    private async Task FailTimesAsync(string email, int times)
    {
        for (var i = 0; i < times; i++)
            await LoginAsync(email, $"YanlisSifre{i}!");
    }

    private static async Task<string> MessageOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<MessageDto>())!.Message;

    /// <summary>Sayaç satırını doğrudan okur/yazar — kilidin süresini "geçmişe almak" için.</summary>
    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    // ---- Firma girişi ----

    [Fact(DisplayName = "Sınır aşılınca DOĞRU şifre bile kabul edilmiyor")]
    public async Task TooManyFailures_LocksAccount_EvenForCorrectPassword()
    {
        await RegisterTenantAsync("kilit", "admin@kilit.com", "Sifre123!");

        await FailTimesAsync("admin@kilit.com", MaxAttempts);

        // Kilidin bütün anlamı burada: saldırgan doğru şifreyi bulsa bile giremiyor.
        var response = await LoginAsync("admin@kilit.com", "Sifre123!");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("dakika", await MessageOfAsync(response));
    }

    [Fact(DisplayName = "Kilidi açan deneme de kilit mesajını görüyor (sessizce kilitlenmiyor)")]
    public async Task AttemptThatTriggersLock_ReportsTheLock()
    {
        await RegisterTenantAsync("kilit2", "admin@kilit2.com", "Sifre123!");

        await FailTimesAsync("admin@kilit2.com", MaxAttempts - 1);
        var last = await LoginAsync("admin@kilit2.com", "YineYanlis!");

        Assert.Contains("dakika", await MessageOfAsync(last));
    }

    [Fact(DisplayName = "Sınırın altında kalan deneme sayısı girişi engellemiyor")]
    public async Task BelowLimit_LoginStillWorks()
    {
        await RegisterTenantAsync("esik", "admin@esik.com", "Sifre123!");

        await FailTimesAsync("admin@esik.com", MaxAttempts - 1);
        var response = await LoginAsync("admin@esik.com", "Sifre123!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(DisplayName = "Başarılı giriş sayacı sıfırlıyor")]
    public async Task SuccessfulLogin_ResetsCounter()
    {
        await RegisterTenantAsync("sifirla", "admin@sifirla.com", "Sifre123!");

        // Dört yanlış, arada bir doğru, sonra yine dört yanlış: sayaç sıfırlanmasaydı
        // toplam sekiz denemede çoktan kilitlenmiş olurdu.
        await FailTimesAsync("admin@sifirla.com", MaxAttempts - 1);
        (await LoginAsync("admin@sifirla.com", "Sifre123!")).EnsureSuccessStatusCode();
        await FailTimesAsync("admin@sifirla.com", MaxAttempts - 1);

        var response = await LoginAsync("admin@sifirla.com", "Sifre123!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(DisplayName = "Kilit süresi dolunca giriş yeniden açılıyor")]
    public async Task AfterLockExpires_LoginWorksAgain()
    {
        await RegisterTenantAsync("sure", "admin@sure.com", "Sifre123!");
        await FailTimesAsync("admin@sure.com", MaxAttempts);

        // Kilidin bitmesini beklemek yerine kaydı geçmişe alıyoruz: test gerçek zamana
        // bağlı olmamalı (15 dakika bekleyen bir test yazılamaz).
        await WithDbAsync(async db =>
            await db.LoginAttempts
                .Where(a => a.Email == "admin@sure.com")
                .ExecuteUpdateAsync(s => s.SetProperty(
                    a => a.LockedUntil, DateTime.UtcNow.AddMinutes(-1))));

        var response = await LoginAsync("admin@sure.com", "Sifre123!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Hesap sayımı (enumeration) ----

    [Fact(DisplayName = "Kayıtlı OLMAYAN e-posta da kilitleniyor: kilit mesajı hesabın " +
                        "varlığını ele vermiyor")]
    public async Task UnknownEmail_LocksToo_SoMessageLeaksNothing()
    {
        await RegisterTenantAsync("sizinti", "admin@sizinti.com", "Sifre123!");

        await FailTimesAsync("admin@sizinti.com", MaxAttempts);
        await FailTimesAsync("hicyok@sizinti.com", MaxAttempts);

        var known = await LoginAsync("admin@sizinti.com", "Sifre123!");
        var unknown = await LoginAsync("hicyok@sizinti.com", "Sifre123!");

        // İki cevap birebir aynı olmalı. Farklı olsalardı, beş yanlış şifre yollamak
        // bir e-postanın sistemde kayıtlı olup olmadığını öğrenmeye yeterdi.
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await MessageOfAsync(known), await MessageOfAsync(unknown));
    }

    [Fact(DisplayName = "Kilit büyük/küçük harf değiştirilerek atlatılamıyor")]
    public async Task Lock_CannotBeBypassed_ByChangingLetterCase()
    {
        await RegisterTenantAsync("harf", "admin@harf.com", "Sifre123!");

        await FailTimesAsync("ADMIN@HARF.COM", MaxAttempts);

        var response = await LoginAsync("admin@harf.com", "Sifre123!");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("dakika", await MessageOfAsync(response));
    }

    // ---- Eşzamanlılık: sayaç paralel istekle sulandırılamaz ----

    [Fact(DisplayName = "Paralel başarısız denemeler sayacı SULANDIRAMIYOR: " +
                        "yirmi eşzamanlı deneme yirmi kez sayılıyor")]
    public async Task ParallelFailures_AreAllCounted()
    {
        // Kod incelemesinde bulunan kusurun testi. Artırım oku-değiştir-yaz biçiminde
        // yazıldığında aynı anda gelen istekler aynı değeri okuyup aynı değeri yazıyordu:
        // yirmi şifre denemesi karşılığında sayaç yalnız BİR artıyor, beş denemelik sınır
        // fiilen "beş TUR" sınırına dönüyordu. Artırım tek atomik SQL ifadesine taşındı.
        await RegisterTenantAsync("paralel", "admin@paralel.com", "Sifre123!");

        const int paralelDeneme = 20;

        // Hepsi AYNI ANDA yollanıyor — sıralı gönderim bu kusuru göstermez.
        var istekler = Enumerable.Range(0, paralelDeneme)
            .Select(i => LoginAsync("admin@paralel.com", $"Yanlis{i}!"))
            .ToArray();

        await Task.WhenAll(istekler);
        foreach (var istek in istekler) (await istek).Dispose();

        // Yirmi denemenin beşincisi kilidi açar, sayaç sıfırlanır ve sayım yeniden başlar.
        // Sayaç kaybolmadıysa hesap kilitli olmalı: DOĞRU şifre bile geçmemeli.
        var response = await LoginAsync("admin@paralel.com", "Sifre123!");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("dakika", await MessageOfAsync(response));
    }

    // ---- Şifre değiştirme kapısı ----

    [Fact(DisplayName = "Şifre değiştirmede mevcut şifre sınırsız denenemiyor")]
    public async Task ChangePassword_IsThrottled()
    {
        // Sızmış bir erişim token'ının 15 dakikalık ömrü, sınır yokken mevcut şifreyi
        // kaba kuvvetle aramaya yetiyordu; bulunursa geçici erişim KALICI erişime dönerdi.
        await RegisterTenantAsync("pw", "admin@pw.com", "Sifre123!");

        var login = await LoginAsync("admin@pw.com", "Sifre123!");
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!.Token;

        using var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new("Bearer", token);

        for (var i = 0; i < MaxAttempts; i++)
            await authed.PostAsJsonAsync("/api/auth/change-password",
                new { currentPassword = $"Yanlis{i}!", newPassword = "YeniSifre123!" });

        // Sınır aşıldı: artık DOĞRU mevcut şifre de kabul edilmiyor.
        var response = await authed.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Sifre123!", newPassword = "YeniSifre123!" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // ---- E-posta kimliği tek tanımlı ----

    [Fact(DisplayName = "Aynı e-posta farklı harf büyüklüğüyle İKİNCİ kez kaydolamıyor")]
    public async Task Email_IsCaseInsensitiveForRegistration()
    {
        // Sayaç e-postayı küçük harfe indirgerken kayıt sorgusu duyarlı karşılaştırıyordu:
        // aynı posta kutusuna ait iki ayrı hesap açılabiliyordu.
        await RegisterTenantAsync("harfbir", "ali@harf.com", "Sifre123!");

        var ikinci = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = "harfiki", email = "Ali@Harf.com", password = "Sifre123!" });

        Assert.Equal(HttpStatusCode.Conflict, ikinci.StatusCode);
    }

    [Fact(DisplayName = "Kullanıcı adresini farklı harf büyüklüğüyle yazınca " +
                        "KENDİ hesabına giriyor (kilitlemiyor)")]
    public async Task Email_CaseDifference_StillLogsIn()
    {
        // Kusurun ters yönü: "Ali@x.com" olarak kayıtlı kullanıcı küçük harfle yazınca
        // hesabını bulamıyor, beş denemede sayaç aynı anahtarı kullandığı için KENDİ
        // hesabını kilitliyordu.
        await RegisterTenantAsync("harfuc", "Veli@Harf.com", "Sifre123!");

        var response = await LoginAsync("veli@harf.com", "Sifre123!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- İki kapının ayrılığı ----

    [Fact(DisplayName = "Platform girişi ayrı sayılıyor: firma tarafındaki kilit onu kapatmıyor")]
    public async Task PlatformGate_IsCountedSeparately()
    {
        // Aynı e-postayla firma kapısında kilit açılıyor…
        await FailTimesAsync(ApiFactory.PlatformAdminEmail, MaxAttempts);

        // …platform kapısı etkilenmemeli: farklı hesap, farklı sayaç.
        var response = await PlatformLoginAsync(
            ApiFactory.PlatformAdminEmail, ApiFactory.PlatformAdminPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(DisplayName = "Platform girişinde de sınır var")]
    public async Task PlatformGate_IsAlsoThrottled()
    {
        for (var i = 0; i < MaxAttempts; i++)
            await PlatformLoginAsync(ApiFactory.PlatformAdminEmail, $"Yanlis{i}!");

        var response = await PlatformLoginAsync(
            ApiFactory.PlatformAdminEmail, ApiFactory.PlatformAdminPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("dakika", await MessageOfAsync(response));
    }

    // ---- Bakım ----

    [Fact(DisplayName = "Bakım eski sayaçları düşürüyor ama SÜREN kilidi düşürmüyor")]
    public async Task Cleanup_DropsOldRows_ButKeepsLiveLock()
    {
        var old = DateTime.UtcNow.AddDays(-30);

        await WithDbAsync(async db =>
        {
            db.LoginAttempts.AddRange(
                // Eski ve kilitsiz → gitmeli.
                new LoginAttempt
                {
                    Id = Guid.NewGuid(), Scope = LoginScopes.Tenant,
                    Email = "eski@bakim.com", FailedCount = 2, LastFailedAt = old
                },
                // Eski ama kilidi HÂLÂ SÜRÜYOR → kalmalı. Bakımın süren bir kilidi
                // düşürmesi, saldırganın önünü bakım eliyle açmak olurdu.
                new LoginAttempt
                {
                    Id = Guid.NewGuid(), Scope = LoginScopes.Tenant,
                    Email = "kilitli@bakim.com", FailedCount = 0, LastFailedAt = old,
                    LockedUntil = DateTime.UtcNow.AddMinutes(10)
                });

            return await db.SaveChangesAsync();
        });

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ILoginThrottle>().CleanAsync();

        var kalanlar = await WithDbAsync(db =>
            db.LoginAttempts.Select(a => a.Email).ToListAsync());

        Assert.DoesNotContain("eski@bakim.com", kalanlar);
        Assert.Contains("kilitli@bakim.com", kalanlar);
    }
}
