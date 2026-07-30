using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeriYonetim.Api.Data;
using Xunit.Abstractions;

namespace VeriYonetim.Api.Tests;

/// <summary>
/// Davet akışı ve şifre yönetimi testleri. Merkezdeki iddia şu:
/// ŞİFREYİ YALNIZCA KULLANICININ KENDİSİ BİLİR — Admin ne kullanıcı oluştururken
/// ne de şifre sıfırlarken bir şifre girer ya da görür.
/// </summary>
public class AccountTokenTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public AccountTokenTests(ApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _output = output;
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Yardımcılar ----

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record InviteDto(string Token, string Email, string? Role, DateTime ExpiresAt,
        string Purpose);

    private record InfoDto(string Purpose, string Email, string? Role, string TenantName,
        DateTime ExpiresAt);

    private record UserRow(Guid Id, string Email, string Role, Guid TenantId);

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<TokenResponse> RegisterTenantAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = name, email, password = "Sifre123!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private async Task<HttpResponseMessage> LoginRawAsync(string email, string password) =>
        await _client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private async Task<InviteDto> InviteAsync(string adminToken, string email, string role)
    {
        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users/invite",
            adminToken, new { email, role }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InviteDto>())!;
    }

    private async Task<HttpResponseMessage> AcceptAsync(string token, string password) =>
        await _client.PostAsJsonAsync($"/api/invitations/{token}/accept", new { password });

    // ---- Davet akışı ----

    [Fact(DisplayName = "Davet: kullanıcı şifresini KENDİ belirler, hesap oluşur ve giriş yapar")]
    public async Task Invite_UserSetsOwnPassword_AndCanLogin()
    {
        var admin = await RegisterTenantAsync("davet", "admin@davet.com");

        var invite = await InviteAsync(admin.Token, "yeni@davet.com", "Editor");
        var accept = await AcceptAsync(invite.Token, "KendiSifrem123!");
        var login = await LoginRawAsync("yeni@davet.com", "KendiSifrem123!");

        accept.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var user = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal("Editor", user.Role);
        Assert.Equal(admin.TenantId, user.TenantId);
        _output.WriteLine("✓ Kanıt: Admin şifreyi hiç görmeden kullanıcı oluştu; şifreyi kullanıcı " +
                          "kendisi belirledi ve o şifreyle giriş yaptı. Rol davetteki gibi (Editor).");
    }

    [Fact(DisplayName = "Davet: ham token veritabanında SAKLANMAZ (yalnız SHA-256 özeti)")]
    public async Task Invite_RawTokenIsNotStored()
    {
        var admin = await RegisterTenantAsync("hash", "admin@hash.com");
        var invite = await InviteAsync(admin.Token, "yeni@hash.com", "Viewer");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.AccountTokens.IgnoreQueryFilters().ToListAsync();

        var row = Assert.Single(stored);
        Assert.NotEqual(invite.Token, row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length); // SHA-256 → 64 onaltılık karakter
        _output.WriteLine("✓ Kanıt: Veritabanında ham token yok, yalnız 64 karakterlik özet var — " +
                          "veritabanı sızsa bile çalışan bir davet bağlantısı üretilemez.");
    }

    [Fact(DisplayName = "Davet: token TEK kullanımlıktır, ikinci kez kabul edilemez")]
    public async Task Invite_TokenIsSingleUse()
    {
        var admin = await RegisterTenantAsync("tekkullanim", "admin@tek.com");
        var invite = await InviteAsync(admin.Token, "yeni@tek.com", "Viewer");

        var first = await AcceptAsync(invite.Token, "IlkSifre123!");
        var second = await AcceptAsync(invite.Token, "BaskaSifre123!");

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        // İkinci deneme şifreyi değiştirmemiş olmalı.
        var login = await LoginRawAsync("yeni@tek.com", "IlkSifre123!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _output.WriteLine("✓ Kanıt: Harcanmış davet bağlantısı ikinci kez çalışmadı; " +
                          "elden ele dolaşan bir bağlantı hesabı ele geçirmeye yetmiyor.");
    }

    [Fact(DisplayName = "Davet: geçersiz token 404 döner")]
    public async Task Invite_InvalidToken_NotFound()
    {
        var inspect = await _client.GetAsync("/api/invitations/uydurma-token-123");
        var accept = await AcceptAsync("uydurma-token-123", "Sifre123!");

        Assert.Equal(HttpStatusCode.NotFound, inspect.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
    }

    [Fact(DisplayName = "Davet: bağlantı açılınca firma adı ve rol görünür (token harcanmadan)")]
    public async Task Invite_Inspect_ShowsContextWithoutConsuming()
    {
        var admin = await RegisterTenantAsync("Bilgi Firma", "admin@bilgi.com");
        var invite = await InviteAsync(admin.Token, "yeni@bilgi.com", "Admin");

        var response = await _client.GetAsync($"/api/invitations/{invite.Token}");
        var info = (await response.Content.ReadFromJsonAsync<InfoDto>())!;

        Assert.Equal("Invite", info.Purpose);
        Assert.Equal("yeni@bilgi.com", info.Email);
        Assert.Equal("Admin", info.Role);
        Assert.Equal("Bilgi Firma", info.TenantName);

        // Görüntülemek token'ı harcamamalı: kabul hâlâ çalışmalı.
        (await AcceptAsync(invite.Token, "Sifre123!")).EnsureSuccessStatusCode();
    }

    [Fact(DisplayName = "Davet: yeni davet öncekini geçersizleştirir (tek geçerli bağlantı)")]
    public async Task Invite_NewInviteInvalidatesPrevious()
    {
        var admin = await RegisterTenantAsync("cift", "admin@cift.com");

        var first = await InviteAsync(admin.Token, "yeni@cift.com", "Viewer");
        var second = await InviteAsync(admin.Token, "yeni@cift.com", "Editor");

        var oldOne = await AcceptAsync(first.Token, "Sifre123!");
        var newOne = await AcceptAsync(second.Token, "Sifre123!");

        Assert.Equal(HttpStatusCode.NotFound, oldOne.StatusCode);
        newOne.EnsureSuccessStatusCode();
        _output.WriteLine("✓ Kanıt: Yeni davet üretilince eski bağlantı ölüyor — aynı anda birden " +
                          "fazla geçerli davet dolaşmıyor.");
    }

    [Fact(DisplayName = "Davet: askıya alınmış firmanın daveti işlemez")]
    public async Task Invite_SuspendedTenant_Rejected()
    {
        var admin = await RegisterTenantAsync("aski-davet", "admin@askidavet.com");
        var invite = await InviteAsync(admin.Token, "yeni@askidavet.com", "Viewer");

        // Platform yöneticisi firmayı askıya alır.
        var platformLogin = await _client.PostAsJsonAsync("/api/platform/auth/login",
            new { email = ApiFactory.PlatformAdminEmail, password = ApiFactory.PlatformAdminPassword });
        var platformToken = (await platformLogin.Content
            .ReadFromJsonAsync<Dictionary<string, object>>())!["token"].ToString()!;

        await _client.SendAsync(WithToken(HttpMethod.Put,
            $"/api/platform/tenants/{admin.TenantId}/status", platformToken, new { isActive = false }));

        var accept = await AcceptAsync(invite.Token, "Sifre123!");

        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        _output.WriteLine("✓ Kanıt: Askı her kapıyı kapatıyor — açık bir davet bağlantısı bile " +
                          "askıya alınmış firmaya yeni kullanıcı sokamıyor.");
    }

    [Fact(DisplayName = "Davet: Admin isteğinde şifre alanı YOKTUR (gönderilse bile yok sayılır)")]
    public async Task Invite_IgnoresAnyPasswordFieldFromAdmin()
    {
        var admin = await RegisterTenantAsync("sifresiz", "admin@sifresiz.com");

        // Admin fazladan bir "password" alanı göndermeye çalışsa bile DTO'da karşılığı
        // yok — bu şifre hiçbir yere yazılmaz ve o şifreyle giriş yapılamaz.
        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users/invite",
            admin.Token,
            new { email = "yeni@sifresiz.com", role = "Viewer", password = "AdminBunuSecti1!" }));
        var invite = (await response.Content.ReadFromJsonAsync<InviteDto>())!;
        await AcceptAsync(invite.Token, "KullaniciSecti1!");

        var withAdminsPassword = await LoginRawAsync("yeni@sifresiz.com", "AdminBunuSecti1!");
        var withOwnPassword = await LoginRawAsync("yeni@sifresiz.com", "KullaniciSecti1!");

        Assert.Equal(HttpStatusCode.Unauthorized, withAdminsPassword.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withOwnPassword.StatusCode);
        _output.WriteLine("✓ Kanıt: Admin'in gönderdiği şifre hiçbir etkiye sahip değil — " +
                          "yalnız kullanıcının kendi belirlediği şifre çalışıyor.");
    }

    // ---- Şifre sıfırlama (Admin bağlantı üretir, şifreyi görmez) ----

    [Fact(DisplayName = "Sıfırlama: Admin bağlantı üretir, şifreyi kullanıcı belirler")]
    public async Task PasswordReset_AdminCreatesLink_UserSetsPassword()
    {
        var admin = await RegisterTenantAsync("sifirla", "admin@sifirla.com");
        var invite = await InviteAsync(admin.Token, "uye@sifirla.com", "Viewer");
        await AcceptAsync(invite.Token, "EskiSifre123!");

        var users = (await (await _client.SendAsync(
                WithToken(HttpMethod.Get, "/api/users", admin.Token)))
            .Content.ReadFromJsonAsync<List<UserRow>>())!;
        var target = users.Single(u => u.Email == "uye@sifirla.com");

        var resetResponse = await _client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/users/{target.Id}/reset-password", admin.Token));
        resetResponse.EnsureSuccessStatusCode();
        var reset = (await resetResponse.Content.ReadFromJsonAsync<InviteDto>())!;

        Assert.Equal("PasswordReset", reset.Purpose);

        await AcceptAsync(reset.Token, "YeniSifre456!");

        var withOld = await LoginRawAsync("uye@sifirla.com", "EskiSifre123!");
        var withNew = await LoginRawAsync("uye@sifirla.com", "YeniSifre456!");

        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
        _output.WriteLine("✓ Kanıt: Admin yalnız bir bağlantı üretti; yeni şifreyi görmedi, " +
                          "kullanıcı kendisi belirledi.");
    }

    [Fact(DisplayName = "Sıfırlama: Admin olmayan kullanıcı bağlantı üretemez (403)")]
    public async Task PasswordReset_NonAdmin_Forbidden()
    {
        var admin = await RegisterTenantAsync("sifirla-yetki", "admin@syetki.com");
        var invite = await InviteAsync(admin.Token, "editor@syetki.com", "Editor");
        await AcceptAsync(invite.Token, "Sifre123!");
        var editor = (await (await LoginRawAsync("editor@syetki.com", "Sifre123!"))
            .Content.ReadFromJsonAsync<TokenResponse>())!;

        var response = await _client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/users/{admin.UserId}/reset-password", editor.Token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(DisplayName = "Sıfırlama: başka tenant'ın kullanıcısı için bağlantı üretilemez (404)")]
    public async Task PasswordReset_CrossTenant_NotFound()
    {
        var tenantA = await RegisterTenantAsync("sifirla-a", "admin@sa.com");
        var tenantB = await RegisterTenantAsync("sifirla-b", "admin@sb.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/users/{tenantB.UserId}/reset-password", tenantA.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        _output.WriteLine("✓ Kanıt: A firmasının Admin'i B firmasının kullanıcısı için sıfırlama " +
                          "bağlantısı üretemedi — izolasyon burada da geçerli.");
    }

    [Fact(DisplayName = "Sıfırlama: şifre değişince eski oturumlar (refresh token) ölür")]
    public async Task PasswordReset_RevokesExistingSessions()
    {
        var admin = await RegisterTenantAsync("sifirla-oturum", "admin@soturum.com");
        var invite = await InviteAsync(admin.Token, "uye@soturum.com", "Viewer");
        await AcceptAsync(invite.Token, "EskiSifre123!");

        // Kullanıcı giriş yapar → elinde refresh token var.
        var session = (await (await LoginRawAsync("uye@soturum.com", "EskiSifre123!"))
            .Content.ReadFromJsonAsync<TokenResponse>())!;

        var users = (await (await _client.SendAsync(
                WithToken(HttpMethod.Get, "/api/users", admin.Token)))
            .Content.ReadFromJsonAsync<List<UserRow>>())!;
        var target = users.Single(u => u.Email == "uye@soturum.com");

        var reset = (await (await _client.SendAsync(WithToken(HttpMethod.Post,
                $"/api/users/{target.Id}/reset-password", admin.Token)))
            .Content.ReadFromJsonAsync<InviteDto>())!;
        await AcceptAsync(reset.Token, "YeniSifre456!");

        var refresh = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = session.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        _output.WriteLine("✓ Kanıt: Sıfırlama sonrası eski refresh token çalışmıyor — " +
                          "hesabı ele geçiren birinin oturumu şifre değişince kapanıyor.");
    }

    // ---- Kendi şifresini değiştirme ----

    [Fact(DisplayName = "Şifre değiştirme: doğru mevcut şifreyle çalışır, eski şifre geçersizleşir")]
    public async Task ChangePassword_Works()
    {
        var admin = await RegisterTenantAsync("degistir", "admin@degistir.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post,
            "/api/auth/change-password", admin.Token,
            new { currentPassword = "Sifre123!", newPassword = "YeniSifre456!" }));
        response.EnsureSuccessStatusCode();

        var withOld = await LoginRawAsync("admin@degistir.com", "Sifre123!");
        var withNew = await LoginRawAsync("admin@degistir.com", "YeniSifre456!");

        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    [Fact(DisplayName = "Şifre değiştirme: yanlış mevcut şifre reddedilir (400)")]
    public async Task ChangePassword_WrongCurrent_Rejected()
    {
        var admin = await RegisterTenantAsync("degistir-yanlis", "admin@dyanlis.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post,
            "/api/auth/change-password", admin.Token,
            new { currentPassword = "AlakasizSifre1!", newPassword = "YeniSifre456!" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Mevcut şifre şart — çalınmış bir token'la şifre değiştirilip " +
                          "erişim kalıcı hâle getirilemiyor.");
    }

    [Fact(DisplayName = "Şifre değiştirme: yeni şifre eskisiyle aynı olamaz (400)")]
    public async Task ChangePassword_SameAsOld_Rejected()
    {
        var admin = await RegisterTenantAsync("degistir-ayni", "admin@dayni.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post,
            "/api/auth/change-password", admin.Token,
            new { currentPassword = "Sifre123!", newPassword = "Sifre123!" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Şifre değiştirme: 8 karakterden kısa şifre reddedilir (400)")]
    public async Task ChangePassword_TooShort_Rejected()
    {
        var admin = await RegisterTenantAsync("degistir-kisa", "admin@dkisa.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post,
            "/api/auth/change-password", admin.Token,
            new { currentPassword = "Sifre123!", newPassword = "kisa" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Şifre değiştirme: token'sız yapılamaz (401)")]
    public async Task ChangePassword_RequiresAuth()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Sifre123!", newPassword = "YeniSifre456!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "Şifre değiştirme: tüm oturumlar kapanır (refresh token iptal)")]
    public async Task ChangePassword_RevokesAllSessions()
    {
        var admin = await RegisterTenantAsync("degistir-oturum", "admin@doturum.com");

        await _client.SendAsync(WithToken(HttpMethod.Post, "/api/auth/change-password",
            admin.Token, new { currentPassword = "Sifre123!", newPassword = "YeniSifre456!" }));

        var refresh = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = admin.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        _output.WriteLine("✓ Kanıt: Şifre değişince eski refresh token'lar iptal — aksi hâlde " +
                          "şifresi çalınan kullanıcı şifresini değiştirse bile saldırganın " +
                          "oturumu 7 gün daha yaşardı.");
    }

    [Fact(DisplayName = "Şifre değiştirme: başkasının şifresi değiştirilemez (hedef token'dan gelir)")]
    public async Task ChangePassword_CannotTargetAnotherUser()
    {
        var admin = await RegisterTenantAsync("degistir-baskasi", "admin@dbaskasi.com");
        var invite = await InviteAsync(admin.Token, "uye@dbaskasi.com", "Viewer");
        await AcceptAsync(invite.Token, "UyeSifre123!");

        // Admin kendi token'ıyla çağırır; istekte hedef kullanıcı alanı YOK — hedef
        // her zaman token'daki kullanıcıdır. Dolayısıyla üyenin şifresi etkilenmez.
        await _client.SendAsync(WithToken(HttpMethod.Post, "/api/auth/change-password",
            admin.Token, new { currentPassword = "Sifre123!", newPassword = "YeniSifre456!" }));

        var memberLogin = await LoginRawAsync("uye@dbaskasi.com", "UyeSifre123!");

        Assert.Equal(HttpStatusCode.OK, memberLogin.StatusCode);
        _output.WriteLine("✓ Kanıt: Admin'in şifre değiştirmesi üyenin şifresini etkilemedi — " +
                          "hedef istekten değil token'dan geliyor.");
    }
}
