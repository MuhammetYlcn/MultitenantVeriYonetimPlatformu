using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Tests;

public class IsolationTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public IsolationTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // @BeforeEach karşılığı: her test temiz veritabanıyla başlar.
    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record UserRow(Guid Id, string Email, string Role, Guid TenantId);

    private async Task<TokenResponse> RegisterTenantAsync(string slug, string email)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = slug, tenantSlug = slug, email, password = "Sifre123!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private async Task<TokenResponse> LoginAsync(string slug, string email)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { tenantSlug = slug, email, password = "Sifre123!" });
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

    [Fact]
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
    }

    [Fact]
    public async Task SameEmail_CanRegisterInDifferentTenants()
    {
        // E-posta benzersizliği tenant başına — (TenantId, Email) unique index'inin kanıtı.
        var a = await RegisterTenantAsync("mail-a", "ortak@mail.com");
        var b = await RegisterTenantAsync("mail-b", "ortak@mail.com");

        Assert.NotEqual(a.TenantId, b.TenantId);
        Assert.NotEqual(a.UserId, b.UserId);
    }

    [Fact]
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
    }

    // ---- RBAC ----

    [Fact]
    public async Task AdminRole_CanCreateUser()
    {
        var admin = await RegisterTenantAsync("rbac-a", "admin@rbac.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users",
            admin.Token, new { email = "uye@rbac.com", password = "Sifre123!", role = "User" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UserRole_CannotCreateUser()
    {
        var admin = await RegisterTenantAsync("rbac-b", "admin@rbacb.com");
        await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users",
            admin.Token, new { email = "uye@rbacb.com", password = "Sifre123!", role = "User" }));

        var member = await LoginAsync("rbac-b", "uye@rbacb.com");
        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users",
            member.Token, new { email = "davetsiz@rbacb.com", password = "Sifre123!", role = "User" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_CannotListUsers()
    {
        var response = await _client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InjectionSlug_IsRejectedAtValidation()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            tenantName = "Kotu",
            tenantSlug = "kotu'; drop schema public; --",
            email = "kotu@evil.com",
            password = "Sifre123!"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
