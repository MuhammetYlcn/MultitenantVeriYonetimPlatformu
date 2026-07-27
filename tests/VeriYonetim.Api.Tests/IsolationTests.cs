using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeriYonetim.Api.Data;
using Xunit.Abstractions;

namespace VeriYonetim.Api.Tests;

public class IsolationTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    // xUnit her teste bunu enjekte eder; WriteLine ile yazdığımız satırlar o testin
    // "output" panosunda görünür (VS Code'da testi seçince, CLI'da detaylı logger'da).
    private readonly ITestOutputHelper _output;

    public IsolationTests(ApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _output = output;
    }

    // @BeforeEach karşılığı: her test temiz veritabanıyla başlar.
    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record UserRow(Guid Id, string Email, string Role, Guid TenantId);

    private async Task<TokenResponse> RegisterTenantAsync(string name, string email)
    {
        // Slug istemci tarafından gönderilmez; sunucu firma adından türetir.
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = name, email, password = "Sifre123!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private async Task<TokenResponse> LoginAsync(string email)
    {
        // E-posta global benzersiz → giriş yalnız e-posta + şifre ile.
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { email, password = "Sifre123!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    // ---- İzolasyon ----

    [Fact(DisplayName = "İzolasyon: kullanıcı listesi yalnız kendi tenant'ının kullanıcılarını döndürür")]
    public async Task UserListing_ReturnsOnlyOwnTenantsUsers()
    {
        var tenantA = await RegisterTenantAsync("iso-a", "ali@a.com");
        var tenantB = await RegisterTenantAsync("iso-b", "ayse@b.com");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/users", tenantA.Token));
        var users = (await response.Content.ReadFromJsonAsync<List<UserRow>>())!;

        Assert.All(users, u => Assert.Equal(tenantA.TenantId, u.TenantId));
        Assert.DoesNotContain(users, u => u.Email == "ayse@b.com");
        Assert.Contains(users, u => u.Email == "ali@a.com");
        _output.WriteLine("✓ Kanıt: A tenant'ının kullanıcı listesinde yalnız kendi kullanıcıları var; B'nin e-postası (ayse@b.com) görünmüyor.");
    }

    [Fact(DisplayName = "Aynı e-posta ikinci kez kaydolamaz (409 Conflict)")]
    public async Task SameEmail_CannotRegisterTwice()
    {
        // E-posta global benzersiz — aynı e-posta ikinci bir tenant'a kaydedilemez.
        await RegisterTenantAsync("mail-a", "ortak@mail.com");

        var second = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = "Baska Firma", email = "ortak@mail.com", password = "Sifre123!" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        _output.WriteLine("✓ Kanıt: Aynı e-posta (ortak@mail.com) ikinci bir firmaya kaydolmaya çalışınca 409 Conflict döndü.");
    }

    [Fact(DisplayName = "Kayıt, tenant'a özel PostgreSQL şemasını oluşturur")]
    public async Task Register_CreatesTenantSchema()
    {
        await RegisterTenantAsync("sema-test", "sema@test.com");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schemas = await db.Database
            .SqlQuery<string>($"""
                SELECT schema_name AS "Value" FROM information_schema.schemata
                """)
            .ToListAsync();

        Assert.Contains("tenant_sema_test", schemas);
        _output.WriteLine("✓ Kanıt: Kayıt sonrası veritabanında 'tenant_sema_test' şeması oluştu.");
    }

    // ---- RBAC ----

    [Fact(DisplayName = "Admin yeni kullanıcı ekleyebilir (201 Created)")]
    public async Task AdminRole_CanCreateUser()
    {
        var admin = await RegisterTenantAsync("rbac-a", "admin@rbac.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users",
            admin.Token, new { email = "uye@rbac.com", password = "Sifre123!", role = "Viewer" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Admin token'ıyla yeni kullanıcı ekleme 201 Created döndü.");
    }

    [Fact(DisplayName = "Yetkisiz kullanıcı (Admin değil) kullanıcı ekleyemez (403 Forbidden)")]
    public async Task UserRole_CannotCreateUser()
    {
        var admin = await RegisterTenantAsync("rbac-b", "admin@rbacb.com");
        await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users",
            admin.Token, new { email = "uye@rbacb.com", password = "Sifre123!", role = "Editor" }));

        var member = await LoginAsync("uye@rbacb.com");
        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users",
            member.Token, new { email = "davetsiz@rbacb.com", password = "Sifre123!", role = "Viewer" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Yetkisiz (Admin olmayan) kullanıcı kullanıcı eklemeye çalışınca 403 Forbidden döndü.");
    }

    [Fact(DisplayName = "Token'sız (anonim) istek reddedilir (401 Unauthorized)")]
    public async Task Anonymous_CannotListUsers()
    {
        var response = await _client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Token'sız /api/users isteği 401 Unauthorized döndü.");
    }

    [Fact(DisplayName = "Kötü niyetli firma adı güvenle slug'a indirgenir, public şema sağlam (SQL injection yok)")]
    public async Task MaliciousTenantName_IsSlugifiedSafely()
    {
        // Slug artık firma adından türetiliyor; tehlikeli karakterler reddedilmek yerine
        // slug'a indirgenirken atılıyor. Kötü niyetli bir ad güvenle zararsız hâle gelir.
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            tenantName = "Kotu'; DROP SCHEMA public; --",
            email = "kotu@evil.com",
            password = "Sifre123!"
        });

        // Kayıt güvenle tamamlanır (injection yok) ve public şema hâlâ yerinde.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schemas = await db.Database
            .SqlQuery<string>($"""
                SELECT schema_name AS "Value" FROM information_schema.schemata
                """)
            .ToListAsync();

        Assert.Contains("public", schemas);
        Assert.Contains("tenant_kotu_drop_schema_public", schemas);
        _output.WriteLine("✓ Kanıt: Kötü niyetli ad güvenle 'tenant_kotu_drop_schema_public' slug'ına indirgendi; public şema hâlâ yerinde (SQL injection çalışmadı).");
    }

    // ---- 3'lü rol yetkilendirme (Viewer < Editor < Admin) ----

    [Fact(DisplayName = "Viewer veri seti oluşturamaz — yazma yetkisi yok (403)")]
    public async Task Viewer_CannotCreateDataset()
    {
        var admin = await RegisterTenantAsync("role-v", "admin@rolev.com");
        await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users", admin.Token,
            new { email = "viewer@rolev.com", password = "Sifre123!", role = "Viewer" }));
        var viewer = await LoginAsync("viewer@rolev.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets",
            viewer.Token, new { name = "Deneme", description = (string?)null }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Viewer rolü POST /api/datasets'e 403 aldı (yalnız okuyabilir).");
    }

    [Fact(DisplayName = "Editor veri seti oluşturabilir (201)")]
    public async Task Editor_CanCreateDataset()
    {
        var admin = await RegisterTenantAsync("role-e", "admin@rolee.com");
        await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users", admin.Token,
            new { email = "editor@rolee.com", password = "Sifre123!", role = "Editor" }));
        var editor = await LoginAsync("editor@rolee.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets",
            editor.Token, new { name = "Deneme", description = (string?)null }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Editor rolü POST /api/datasets'e 201 aldı (veri yazabilir).");
    }

    [Fact(DisplayName = "Viewer veri setlerini listeleyebilir — okuma Viewer+ herkese açık (200)")]
    public async Task Viewer_CanListDatasets()
    {
        var admin = await RegisterTenantAsync("role-vr", "admin@rolevr.com");
        await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users", admin.Token,
            new { email = "viewer@rolevr.com", password = "Sifre123!", role = "Viewer" }));
        var viewer = await LoginAsync("viewer@rolevr.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Get, "/api/datasets", viewer.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Viewer rolü GET /api/datasets'i 200 ile okuyabildi.");
    }

    // ---- Rol değiştirme (PUT /api/users/{id}/role) ----

    // Testlerde kullanıcı ekleyip rolünü öğrenmek için ortak yardımcı.
    private async Task<UserRow> CreateUserAsync(string adminToken, string email, string role)
    {
        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users",
            adminToken, new { email, password = "Sifre123!", role }));
        response.EnsureSuccessStatusCode();

        var users = (await (await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/users", adminToken)))
            .Content.ReadFromJsonAsync<List<UserRow>>())!;
        return users.Single(u => u.Email == email);
    }

    [Fact(DisplayName = "Admin bir kullanıcının rolünü değiştirebilir ve değişiklik kalıcıdır (200)")]
    public async Task Admin_CanChangeUserRole()
    {
        var admin = await RegisterTenantAsync("rol-a", "admin@rola.com");
        var viewer = await CreateUserAsync(admin.Token, "viewer@rola.com", "Viewer");

        var response = await _client.SendAsync(WithToken(HttpMethod.Put,
            $"/api/users/{viewer.Id}/role", admin.Token, new { role = "Editor" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Kalıcılık: ayrı bir istekte veritabanından okunan rol de değişmiş olmalı.
        var users = (await (await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/users", admin.Token)))
            .Content.ReadFromJsonAsync<List<UserRow>>())!;
        Assert.Equal("Editor", users.Single(u => u.Id == viewer.Id).Role);
        _output.WriteLine("✓ Kanıt: Admin, Viewer'ı Editor yaptı (200) ve yeni rol ayrı bir istekte de Editor olarak okundu.");
    }

    [Fact(DisplayName = "Son yöneticinin rolü düşürülemez (409 Conflict)")]
    public async Task LastAdmin_CannotBeDemoted()
    {
        var admin = await RegisterTenantAsync("rol-son", "admin@rolson.com");
        // Tenant'ta başka kullanıcı var ama Admin yok → admin tek yönetici.
        await CreateUserAsync(admin.Token, "editor@rolson.com", "Editor");

        var response = await _client.SendAsync(WithToken(HttpMethod.Put,
            $"/api/users/{admin.UserId}/role", admin.Token, new { role = "Editor" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Tenant'ın tek yöneticisini düşürme denemesi 409 Conflict ile reddedildi (kullanıcı yönetimi kilitlenmiyor).");
    }

    [Fact(DisplayName = "İkinci bir yönetici atandıktan sonra ilk yönetici düşürülebilir (200)")]
    public async Task Admin_CanBeDemoted_WhenAnotherAdminExists()
    {
        var admin = await RegisterTenantAsync("rol-iki", "admin@roliki.com");
        var ikinci = await CreateUserAsync(admin.Token, "admin2@roliki.com", "Admin");

        var response = await _client.SendAsync(WithToken(HttpMethod.Put,
            $"/api/users/{admin.UserId}/role", admin.Token, new { role = "Viewer" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(Guid.Empty, ikinci.Id);
        _output.WriteLine("✓ Kanıt: Tenant'ta ikinci bir Admin varken ilk Admin Viewer'a düşürülebildi (son-Admin kuralı yalnız gerçekten son Admin'i korur).");
    }

    [Fact(DisplayName = "Admin olmayan kullanıcı rol değiştiremez (403 Forbidden)")]
    public async Task NonAdmin_CannotChangeRole()
    {
        var admin = await RegisterTenantAsync("rol-yetki", "admin@rolyetki.com");
        var editor = await CreateUserAsync(admin.Token, "editor@rolyetki.com", "Editor");
        var editorToken = (await LoginAsync("editor@rolyetki.com")).Token;

        var response = await _client.SendAsync(WithToken(HttpMethod.Put,
            $"/api/users/{editor.Id}/role", editorToken, new { role = "Admin" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Editor rolü kendini Admin yapmayı denedi ve 403 Forbidden aldı (yetki yükseltme engellendi).");
    }

    [Fact(DisplayName = "Başka tenant'ın kullanıcısının rolü değiştirilemez (404 Not Found)")]
    public async Task CrossTenant_RoleChange_Returns404()
    {
        var tenantA = await RegisterTenantAsync("rol-capraz-a", "admin@rolcaprazA.com");
        var tenantB = await RegisterTenantAsync("rol-capraz-b", "admin@rolcaprazB.com");

        // A'nın Admin'i, B'nin kullanıcısının rolünü değiştirmeye çalışıyor.
        var response = await _client.SendAsync(WithToken(HttpMethod.Put,
            $"/api/users/{tenantB.UserId}/role", tenantA.Token, new { role = "Viewer" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        _output.WriteLine("✓ Kanıt: A tenant'ının Admin'i B tenant'ının kullanıcısına erişemedi — global query filter sayesinde 404 döndü (varlığı bile sızmıyor).");
    }

    [Fact(DisplayName = "Geçersiz rol adı reddedilir (400 Bad Request)")]
    public async Task InvalidRole_IsRejected()
    {
        var admin = await RegisterTenantAsync("rol-gecersiz", "admin@rolgecersiz.com");
        var viewer = await CreateUserAsync(admin.Token, "viewer@rolgecersiz.com", "Viewer");

        var response = await _client.SendAsync(WithToken(HttpMethod.Put,
            $"/api/users/{viewer.Id}/role", admin.Token, new { role = "SuperAdmin" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Tanımsız rol ('SuperAdmin') doğrulama katmanında 400 ile reddedildi.");
    }

    [Fact(DisplayName = "Başka tenant'ta kayıtlı e-postayla kullanıcı eklenemez (409 Conflict)")]
    public async Task CreateUser_WithEmailFromAnotherTenant_Returns409()
    {
        // E-posta global benzersiz olduğundan, mükerrer kontrolü tenant sınırını aşmalı;
        // aksi hâlde veritabanı unique index'i patlar ve istemci 500 alırdı.
        await RegisterTenantAsync("mail-capraz-a", "ortak@capraz.com");
        var tenantB = await RegisterTenantAsync("mail-capraz-b", "admin@caprazB.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users",
            tenantB.Token, new { email = "ortak@capraz.com", password = "Sifre123!", role = "Viewer" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        _output.WriteLine("✓ Kanıt: Başka tenant'ta kayıtlı e-postayla kullanıcı ekleme 500 yerine düzgün 409 Conflict döndü.");
    }
}
