using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

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

    private async Task<TokenResponse> RegisterTenantAsync(string name, string email)
    {
        // Slug istemci tarafından gönderilmez; sunucu firma adından türetir.
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = name, email, password = "Sifre123!" });
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

    // ---- Satır listeleme (sayfalama / sıralama / JSONB filtre) ----

    private record RowItemDto(Guid Id, Dictionary<string, JsonElement> Data);
    private record RowListDto(int Page, int PageSize, int Total, int TotalPages, List<RowItemDto> Rows);

    // Ortak veri: 4 satırlık şemalı bir dataset kurup id'sini döndürür.
    private async Task<(string Token, Guid Id)> SeededDatasetAsync(string slug, string email)
    {
        var t = await RegisterTenantAsync(slug, email);
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;
        await UploadSchemaAsync(t.Token, id, "ad,yas,sehir\nAli,30,Ankara");
        await ImportRowsAsync(t.Token, id,
            "ad,yas,sehir\nAli,30,Ankara\nAyse,25,Izmir\nVeli,40,Ankara\nCem,35,Bursa");
        return (t.Token, id);
    }

    private async Task<RowListDto> GetRowsAsync(string token, Guid id, string queryString)
    {
        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/rows?{queryString}", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RowListDto>())!;
    }

    [Fact]
    public async Task GetRows_Paginates()
    {
        var (token, id) = await SeededDatasetAsync("lst-pg", "a@lstpg.com");

        var body = await GetRowsAsync(token, id, "page=1&pageSize=2");

        Assert.Equal(4, body.Total);          // toplam kayıt
        Assert.Equal(2, body.TotalPages);     // 4 / 2
        Assert.Equal(2, body.Rows.Count);     // sayfada 2 satır
    }

    [Fact]
    public async Task GetRows_SortsByNumberDescending()
    {
        var (token, id) = await SeededDatasetAsync("lst-srt", "a@lstsrt.com");

        var body = await GetRowsAsync(token, id, "sort=yas&dir=desc");

        // En büyük yas ilk sırada olmalı (sayısal sıralama, metinsel değil).
        Assert.Equal(40, body.Rows[0].Data["yas"].GetInt32());
        Assert.Equal("Veli", body.Rows[0].Data["ad"].GetString());
    }

    [Fact]
    public async Task GetRows_FiltersByNumberGte()
    {
        var (token, id) = await SeededDatasetAsync("lst-flt", "a@lstflt.com");

        var body = await GetRowsAsync(token, id, "filter=yas:gte:35");

        Assert.Equal(2, body.Total);          // Veli(40) + Cem(35)
        Assert.All(body.Rows, r => Assert.True(r.Data["yas"].GetInt32() >= 35));
    }

    [Fact]
    public async Task GetRows_FiltersByTextContains()
    {
        var (token, id) = await SeededDatasetAsync("lst-cnt", "a@lstcnt.com");

        var body = await GetRowsAsync(token, id, "filter=ad:contains:li");

        // "li" içerenler: Ali, Veli.
        Assert.Equal(2, body.Total);
        Assert.All(body.Rows, r => Assert.Contains("li", r.Data["ad"].GetString()!));
    }

    [Fact]
    public async Task GetRows_CombinesFilterAndSort()
    {
        var (token, id) = await SeededDatasetAsync("lst-cmb", "a@lstcmb.com");

        var body = await GetRowsAsync(token, id, "filter=sehir:eq:Ankara&sort=yas&dir=asc");

        Assert.Equal(2, body.Total);          // Ali(30, Ankara) + Veli(40, Ankara)
        Assert.Equal(30, body.Rows[0].Data["yas"].GetInt32());  // asc: önce Ali
        Assert.Equal(40, body.Rows[1].Data["yas"].GetInt32());
    }

    [Fact]
    public async Task GetRows_UnknownFilterColumn_Returns400()
    {
        var (token, id) = await SeededDatasetAsync("lst-unk", "a@lstunk.com");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/rows?filter=yok:eq:1", token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_GetRows_Returns404()
    {
        var (_, id) = await SeededDatasetAsync("lst-x-a", "a@lstxa.com");
        var b = await RegisterTenantAsync("lst-x-b", "b@lstxb.com");

        // B, A'nın satırlarını listeleyemez — ham SQL'e rağmen sahiplik kontrolü koruyor.
        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/rows", b.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Agregasyon (grup özeti / top-N / zaman serisi) ----

    private record AggBucketDto(string? Key, decimal? Value, int Count);
    private record AggResponseDto(string GroupBy, string Op, string? Metric, string? Bucket,
        List<AggBucketDto> Buckets);

    // Şehir/yaş/tarih/tutar içeren 4 satırlık bir dataset kurar.
    private async Task<(string Token, Guid Id)> SeededAggDatasetAsync(string slug, string email)
    {
        var t = await RegisterTenantAsync(slug, email);
        var id = (await CreateDatasetAsync(t.Token, "S")).Id;
        const string header = "ad,sehir,yas,tarih,tutar";
        await UploadSchemaAsync(t.Token, id, $"{header}\nAli,Ankara,30,2026-01-10,100");
        await ImportRowsAsync(t.Token, id,
            $"{header}\nAli,Ankara,30,2026-01-10,100\nAyse,Izmir,25,2026-01-20,200\n" +
            "Veli,Ankara,40,2026-02-05,150\nCem,Bursa,35,2026-02-15,300");
        return (t.Token, id);
    }

    private async Task<AggResponseDto> AggregateAsync(string token, Guid id, string queryString)
    {
        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/aggregate?{queryString}", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AggResponseDto>())!;
    }

    [Fact]
    public async Task Aggregate_GroupAverage()
    {
        var (token, id) = await SeededAggDatasetAsync("agg-avg", "a@aggavg.com");

        var body = await AggregateAsync(token, id, "groupBy=sehir&op=avg&metric=yas");

        var ankara = body.Buckets.Single(b => b.Key == "Ankara");
        Assert.Equal(35m, ankara.Value);   // (30+40)/2
        Assert.Equal(2, ankara.Count);
        Assert.Equal(25m, body.Buckets.Single(b => b.Key == "Izmir").Value);
    }

    [Fact]
    public async Task Aggregate_TopN_BySumDescending()
    {
        var (token, id) = await SeededAggDatasetAsync("agg-top", "a@aggtop.com");

        var body = await AggregateAsync(token, id,
            "groupBy=sehir&op=sum&metric=tutar&sort=value&dir=desc&limit=2");

        Assert.Equal(2, body.Buckets.Count);              // yalnız en yüksek 2 grup
        Assert.Equal("Bursa", body.Buckets[0].Key);       // 300
        Assert.Equal(300m, body.Buckets[0].Value);
        Assert.Equal("Ankara", body.Buckets[1].Key);      // 100+150=250
        Assert.Equal(250m, body.Buckets[1].Value);
    }

    [Fact]
    public async Task Aggregate_TimeSeries_ByMonth()
    {
        var (token, id) = await SeededAggDatasetAsync("agg-ts", "a@aggts.com");

        var body = await AggregateAsync(token, id,
            "groupBy=tarih&bucket=month&op=sum&metric=tutar&sort=key&dir=asc");

        Assert.Equal(2, body.Buckets.Count);              // Ocak, Şubat
        Assert.StartsWith("2026-01", body.Buckets[0].Key);
        Assert.Equal(300m, body.Buckets[0].Value);        // 100+200
        Assert.StartsWith("2026-02", body.Buckets[1].Key);
        Assert.Equal(450m, body.Buckets[1].Value);        // 150+300
    }

    [Fact]
    public async Task Aggregate_Count_UsesGroupSize()
    {
        var (token, id) = await SeededAggDatasetAsync("agg-cnt", "a@aggcnt.com");

        var body = await AggregateAsync(token, id, "groupBy=sehir&op=count");

        Assert.Equal(2, body.Buckets.Single(b => b.Key == "Ankara").Count);
        Assert.Equal(2m, body.Buckets.Single(b => b.Key == "Ankara").Value);
    }

    [Fact]
    public async Task Aggregate_WithFilter_NarrowsRows()
    {
        var (token, id) = await SeededAggDatasetAsync("agg-flt", "a@aggflt.com");

        // yas>=35 → Veli(Ankara) + Cem(Bursa); her grupta 1'er satır.
        var body = await AggregateAsync(token, id, "groupBy=sehir&op=count&filter=yas:gte:35");

        Assert.Equal(2, body.Buckets.Count);
        Assert.All(body.Buckets, b => Assert.Equal(1, b.Count));
    }

    [Fact]
    public async Task Aggregate_NoGroupBy_ReturnsOverallTotal()
    {
        var (token, id) = await SeededAggDatasetAsync("agg-all", "a@aggall.com");

        // groupBy yok → tüm satırların genel toplamı (tek grup, key null).
        var body = await AggregateAsync(token, id, "op=sum&metric=tutar");

        var bucket = Assert.Single(body.Buckets);
        Assert.Null(bucket.Key);
        Assert.Equal(750m, bucket.Value);   // 100+200+150+300
        Assert.Equal(4, bucket.Count);
    }

    [Fact]
    public async Task Aggregate_UnknownGroupColumn_Returns400()
    {
        var (token, id) = await SeededAggDatasetAsync("agg-unk", "a@aggunk.com");

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/aggregate?groupBy=yok&op=count", token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Aggregate_SumOnTextColumn_Returns400()
    {
        var (token, id) = await SeededAggDatasetAsync("agg-txt", "a@aggtxt.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Get,
            $"/api/datasets/{id}/aggregate?groupBy=sehir&op=sum&metric=ad", token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_Aggregate_Returns404()
    {
        var (_, id) = await SeededAggDatasetAsync("agg-x-a", "a@aggxa.com");
        var b = await RegisterTenantAsync("agg-x-b", "b@aggxb.com");

        var response = await _client.SendAsync(WithToken(HttpMethod.Get,
            $"/api/datasets/{id}/aggregate?groupBy=sehir&op=count", b.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
