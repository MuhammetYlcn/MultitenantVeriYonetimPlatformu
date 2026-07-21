using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

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

    // ---- Şema kaydet / oku / izolasyon (DatasetColumn) ----

    private record SchemaColumn(string Name, string Type, int Ordinal);
    private record SchemaResponse(Guid DatasetId, List<SchemaColumn> Columns);

    // Bir CSV içeriğini multipart/form-data olarak /{id}/schema'ya yükler.
    private async Task<HttpResponseMessage> UploadSchemaAsync(string token, Guid datasetId, string csv)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "test.csv");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/datasets/{datasetId}/schema")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task SetSchema_SavesDetectedColumns()
    {
        var t = await RegisterTenantAsync("sch-save", "a@schsave.com");
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;

        var response = await UploadSchemaAsync(t.Token, id, "ad,yas\nAli,30\nAyse,25");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<SchemaResponse>())!;
        Assert.Equal(2, body.Columns.Count);
        Assert.Equal("ad", body.Columns[0].Name);
        Assert.Equal("text", body.Columns[0].Type);
        Assert.Equal("number", body.Columns[1].Type);
    }

    [Fact]
    public async Task GetSchema_ReturnsSavedColumnsInOrder()
    {
        var t = await RegisterTenantAsync("sch-get", "a@schget.com");
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;
        await UploadSchemaAsync(t.Token, id, "ad,yas,tarih\nAli,30,2026-01-15");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/schema", t.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<SchemaResponse>())!;
        Assert.Equal(3, body.Columns.Count);
        Assert.Equal(new[] { 0, 1, 2 }, body.Columns.Select(c => c.Ordinal).ToArray());
        Assert.Equal("date", body.Columns[2].Type);   // kalıcı: ayrı istekte DB'den okundu
    }

    [Fact]
    public async Task SetSchema_Reupload_ReplacesOldColumns()
    {
        var t = await RegisterTenantAsync("sch-re", "a@schre.com");
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;
        await UploadSchemaAsync(t.Token, id, "ad,yas\nAli,30");

        // Farklı kolonlu dosyayı tekrar yükle → eski kolonlar tümüyle değişmeli.
        await UploadSchemaAsync(t.Token, id, "urun,fiyat,adet\nElma,5,10");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/schema", t.Token));
        var body = (await response.Content.ReadFromJsonAsync<SchemaResponse>())!;

        Assert.Equal(3, body.Columns.Count);
        Assert.Contains(body.Columns, c => c.Name == "urun");
        Assert.DoesNotContain(body.Columns, c => c.Name == "ad");
    }

    [Fact]
    public async Task CrossTenant_GetSchema_Returns404()
    {
        var a = await RegisterTenantAsync("sch-x-a", "a@schxa.com");
        var b = await RegisterTenantAsync("sch-x-b", "b@schxb.com");
        var id = (await CreateDatasetAsync(a.Token, "S")).Id;
        await UploadSchemaAsync(a.Token, id, "ad,yas\nAli,30");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/schema", b.Token));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_SetSchema_Returns404()
    {
        var a = await RegisterTenantAsync("sch-xs-a", "a@schxsa.com");
        var b = await RegisterTenantAsync("sch-xs-b", "b@schxsb.com");
        var id = (await CreateDatasetAsync(a.Token, "S")).Id;

        // B, A'nın datasetine şema yazamaz.
        var response = await UploadSchemaAsync(b.Token, id, "ad,yas\nAli,30");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Toplu import (DatasetRow / JSONB) ----

    private record ImportError(int Row, string Column, string? Value, string ExpectedType);
    private record ImportResult(Guid DatasetId, int TotalRows, int Imported, int Failed,
        List<ImportError> Errors, bool ErrorsTruncated);

    // Bir CSV içeriğini multipart/form-data olarak /{id}/rows'a yükler (import).
    private async Task<HttpResponseMessage> ImportRowsAsync(string token, Guid datasetId, string csv)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "rows.csv");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/datasets/{datasetId}/rows")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task Import_ValidRows_StoresAndUpdatesRowCount()
    {
        var t = await RegisterTenantAsync("imp-ok", "a@impok.com");
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;
        await UploadSchemaAsync(t.Token, id, "ad,yas\nAli,30");   // şema: ad=text, yas=number

        var response = await ImportRowsAsync(t.Token, id, "ad,yas\nAli,30\nAyse,25\nVeli,40");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ImportResult>())!;
        Assert.Equal(3, body.Imported);
        Assert.Equal(0, body.Failed);

        // RowCount kalıcı olarak güncellendi mi? (ayrı istekte DB'den okunuyor)
        var get = await _client.SendAsync(WithToken(HttpMethod.Get, $"/api/datasets/{id}", t.Token));
        var ds = (await get.Content.ReadFromJsonAsync<DatasetRow>())!;
        Assert.Equal(3, ds.RowCount);
    }

    [Fact]
    public async Task Import_InvalidRow_SkippedAndReported()
    {
        var t = await RegisterTenantAsync("imp-bad", "a@impbad.com");
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;
        await UploadSchemaAsync(t.Token, id, "ad,yas\nAli,30");

        // İkinci veri satırında yas sayı değil → o satır elenir, raporlanır.
        var response = await ImportRowsAsync(t.Token, id, "ad,yas\nAli,30\nAyse,sayidegil");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ImportResult>())!;
        Assert.Equal(1, body.Imported);
        Assert.Equal(1, body.Failed);
        var err = Assert.Single(body.Errors);
        Assert.Equal("yas", err.Column);
        Assert.Equal("sayidegil", err.Value);
    }

    [Fact]
    public async Task Import_WithoutSchema_Returns400()
    {
        var t = await RegisterTenantAsync("imp-nosch", "a@impnosch.com");
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;

        // Şema tanımlanmadan import edilemez.
        var response = await ImportRowsAsync(t.Token, id, "ad,yas\nAli,30");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_HeaderMismatch_Returns400()
    {
        var t = await RegisterTenantAsync("imp-mism", "a@impmism.com");
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;
        await UploadSchemaAsync(t.Token, id, "ad,yas\nAli,30");

        // Dosyanın kolonları şemayla uyuşmuyor (yas yerine boy).
        var response = await ImportRowsAsync(t.Token, id, "ad,boy\nAli,180");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_Reupload_ReplacesPreviousRows()
    {
        var t = await RegisterTenantAsync("imp-re", "a@impre.com");
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;
        await UploadSchemaAsync(t.Token, id, "ad,yas\nAli,30");

        await ImportRowsAsync(t.Token, id, "ad,yas\nAli,30\nAyse,25");   // 2 satır
        await ImportRowsAsync(t.Token, id, "ad,yas\nVeli,40");           // değiştir → 1 satır

        var get = await _client.SendAsync(WithToken(HttpMethod.Get, $"/api/datasets/{id}", t.Token));
        var ds = (await get.Content.ReadFromJsonAsync<DatasetRow>())!;
        Assert.Equal(1, ds.RowCount);   // eski 2 satır silindi, yerine 1 satır geldi
    }

    [Fact]
    public async Task CrossTenant_Import_Returns404()
    {
        var a = await RegisterTenantAsync("imp-x-a", "a@impxa.com");
        var b = await RegisterTenantAsync("imp-x-b", "b@impxb.com");
        var id = (await CreateDatasetAsync(a.Token, "S")).Id;
        await UploadSchemaAsync(a.Token, id, "ad,yas\nAli,30");

        // B, A'nın datasetine satır import edemez.
        var response = await ImportRowsAsync(b.Token, id, "ad,yas\nHacker,99");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
