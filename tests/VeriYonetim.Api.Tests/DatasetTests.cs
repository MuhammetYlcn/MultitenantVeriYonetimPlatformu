using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace VeriYonetim.Api.Tests;

// Dataset CRUD + çapraz-tenant izolasyon + validasyon/yetki testleri.
// IsolationTests ile aynı desen: her test temiz DB ile başlar (IAsyncLifetime).
public class DatasetTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public DatasetTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record DatasetRow(Guid Id, string Name, string? Description, int RowCount,
        DateTime CreatedAt, DateTime? UpdatedAt);

    private async Task<TokenResponse> RegisterTenantAsync(string slug, string email)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = slug, tenantSlug = slug, email, password = "Sifre123!" });
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

    private async Task<DatasetRow> CreateDatasetAsync(string token, string name, string? description = null)
    {
        var response = await _client.SendAsync(
            WithToken(HttpMethod.Post, "/api/datasets", token, new { name, description }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DatasetRow>())!;
    }

    // ---- CRUD ----

    [Fact]
    public async Task Create_ReturnsCreatedWithZeroRowCount()
    {
        var t = await RegisterTenantAsync("ds-create", "a@ds.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets",
            t.Token, new { name = "2026 Satislari", description = "Yillik" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DatasetRow>())!;
        Assert.Equal("2026 Satislari", created.Name);
        Assert.Equal(0, created.RowCount);   // yeni set boş
        Assert.Null(created.UpdatedAt);      // henüz güncellenmedi
    }

    [Fact]
    public async Task GetById_ReturnsDataset()
    {
        var t = await RegisterTenantAsync("ds-get", "a@dsget.com");
        var created = await CreateDatasetAsync(t.Token, "Set");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{created.Id}", t.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = (await response.Content.ReadFromJsonAsync<DatasetRow>())!;
        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task Update_ChangesFieldsAndSetsUpdatedAt()
    {
        var t = await RegisterTenantAsync("ds-upd", "a@dsupd.com");
        var created = await CreateDatasetAsync(t.Token, "Eski");

        var response = await _client.SendAsync(WithToken(HttpMethod.Put, $"/api/datasets/{created.Id}",
            t.Token, new { name = "Yeni", description = "Guncel" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<DatasetRow>())!;
        Assert.Equal("Yeni", updated.Name);
        Assert.NotNull(updated.UpdatedAt);   // güncelleme zamanı doldu
    }

    [Fact]
    public async Task Delete_RemovesDataset()
    {
        var t = await RegisterTenantAsync("ds-del", "a@dsdel.com");
        var created = await CreateDatasetAsync(t.Token, "Silinecek");

        var del = await _client.SendAsync(
            WithToken(HttpMethod.Delete, $"/api/datasets/{created.Id}", t.Token));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{created.Id}", t.Token));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnDatasets()
    {
        var a = await RegisterTenantAsync("ds-list-a", "a@dsla.com");
        var b = await RegisterTenantAsync("ds-list-b", "b@dslb.com");
        await CreateDatasetAsync(a.Token, "A-set");
        await CreateDatasetAsync(b.Token, "B-set");

        var response = await _client.SendAsync(WithToken(HttpMethod.Get, "/api/datasets", a.Token));
        var list = (await response.Content.ReadFromJsonAsync<List<DatasetRow>>())!;

        Assert.Contains(list, d => d.Name == "A-set");
        Assert.DoesNotContain(list, d => d.Name == "B-set");
    }

    // ---- Çapraz-tenant izolasyon (en kritik) ----

    [Fact]
    public async Task CrossTenant_GetById_Returns404()
    {
        var a = await RegisterTenantAsync("ds-x-a", "a@dsxa.com");
        var b = await RegisterTenantAsync("ds-x-b", "b@dsxb.com");
        var aDataset = await CreateDatasetAsync(a.Token, "A-gizli");

        // B, A'nın dataset id'sini bilse bile erişemez — query filter + FirstOrDefaultAsync.
        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{aDataset.Id}", b.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_Update_Returns404()
    {
        var a = await RegisterTenantAsync("ds-xu-a", "a@dsxua.com");
        var b = await RegisterTenantAsync("ds-xu-b", "b@dsxub.com");
        var aDataset = await CreateDatasetAsync(a.Token, "A-set");

        var response = await _client.SendAsync(WithToken(HttpMethod.Put, $"/api/datasets/{aDataset.Id}",
            b.Token, new { name = "Ele-gecirildi", description = (string?)null }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_Delete_Returns404_AndOwnerRecordSurvives()
    {
        var a = await RegisterTenantAsync("ds-xd-a", "a@dsxda.com");
        var b = await RegisterTenantAsync("ds-xd-b", "b@dsxdb.com");
        var aDataset = await CreateDatasetAsync(a.Token, "A-set");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Delete, $"/api/datasets/{aDataset.Id}", b.Token));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Kanıt: B'nin başarısız silme denemesi A'nın kaydına dokunmadı.
        var ownerGet = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{aDataset.Id}", a.Token));
        Assert.Equal(HttpStatusCode.OK, ownerGet.StatusCode);
    }

    // ---- Validasyon & yetki ----

    [Fact]
    public async Task Create_WithEmptyName_Returns400()
    {
        var t = await RegisterTenantAsync("ds-val", "a@dsval.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets",
            t.Token, new { name = "", description = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_CannotAccessDatasets()
    {
        var response = await _client.GetAsync("/api/datasets");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetById_NonExistent_Returns404()
    {
        var t = await RegisterTenantAsync("ds-nf", "a@dsnf.com");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{Guid.NewGuid()}", t.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
