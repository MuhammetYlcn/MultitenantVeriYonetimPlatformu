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
