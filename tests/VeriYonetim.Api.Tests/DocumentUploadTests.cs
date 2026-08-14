using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Belge yükleme ucunun KAPI davranışı: yetki, sahiplik, dosya doğrulama, yanıt şekli.
//
// Model burada çağrılmıyor. İlk yedi test zaten modele ulaşmadan reddediliyor; son test
// ise görsel servisi sahte bir gerçeklemeyle değiştiriyor. Modelin ne kadar doğru okuduğu
// ayrı bir iş (tools/belge_ureteci) — burada ölçülen şey ucun kendisi.
public class DocumentUploadTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public DocumentUploadTests(ApiFactory factory)
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

    private record InviteDto(string Token, string Email, string? Role);

    private record ExtractDto(
        Guid DatasetId,
        List<string> Columns,
        List<string[]> Rows,
        List<RowError> Errors,
        List<string> Warnings,
        bool Suspect,
        string Model,
        int PromptTokens,
        int NumCtx,
        int LongEdge,
        int Attempts,
        int DurationMs);

    // ---- yardımcılar ----

    private async Task<TokenResponse> RegisterAsync(string name, string email)
    {
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
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<DatasetRow> CreateDatasetAsync(string token, string name)
    {
        var response = await _client.SendAsync(
            WithToken(HttpMethod.Post, "/api/datasets", token, new { name, description = (string?)null }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DatasetRow>())!;
    }

    // Şemayı gerçek yoldan kuruyoruz (CSV yükleyerek): testin kurduğu şema ile üretimin
    // kurduğu şema aynı olsun.
    private async Task SetSchemaAsync(string token, Guid datasetId, string csv)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "sema.csv");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/datasets/{datasetId}/schema")
        { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await _client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static byte[] Jpeg(int width = 800, int height = 600)
    {
        using var image = new Image<Rgb24>(width, height);
        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer, new JpegEncoder { Quality = 90 });
        return buffer.ToArray();
    }

    private HttpRequestMessage UploadRequest(Guid datasetId, string? token,
        byte[] bytes, string fileName, string contentType)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasets/{datasetId}/document/extract")
        { Content = content };

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    private Task<HttpResponseMessage> UploadAsync(Guid datasetId, string token,
        byte[]? bytes = null, string fileName = "fatura.jpg", string contentType = "image/jpeg") =>
        _client.SendAsync(UploadRequest(datasetId, token, bytes ?? Jpeg(), fileName, contentType));

    // ---- kapı testleri (modele hiç ulaşmaz) ----

    [Fact(DisplayName = "Belge ucu: kimliksiz istek reddedilir (401)")]
    public async Task KimliksizIstekReddedilir()
    {
        var admin = await RegisterAsync("belge-401", "a@b401.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Faturalar");

        var response = await _client.SendAsync(
            UploadRequest(dataset.Id, null, Jpeg(), "fatura.jpg", "image/jpeg"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "Belge ucu: Viewer belge yükleyemez — belge yüklemek veri girişidir (403)")]
    public async Task ViewerYukleyemez()
    {
        var admin = await RegisterAsync("belge-rol", "admin@brol.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Faturalar");

        var invite = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users/invite",
            admin.Token, new { email = "izleyici@brol.com", role = "Viewer" }));
        invite.EnsureSuccessStatusCode();
        var link = (await invite.Content.ReadFromJsonAsync<InviteDto>())!;

        (await _client.PostAsJsonAsync($"/api/invitations/{link.Token}/accept",
            new { password = "KendiSifrem123!" })).EnsureSuccessStatusCode();

        var login = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "izleyici@brol.com", password = "KendiSifrem123!" });
        var viewer = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal("Viewer", viewer.Role);

        var response = await UploadAsync(dataset.Id, viewer.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(DisplayName = "Belge ucu: başka firmanın veri setine yüklenemez (404 — varlığı sızdırmaz)")]
    public async Task CaprazTenantYuklemeReddedilir()
    {
        var a = await RegisterAsync("belge-a", "a@bcapraz.com");
        var b = await RegisterAsync("belge-b", "b@bcapraz.com");

        var dataset = await CreateDatasetAsync(a.Token, "A Faturalari");
        await SetSchemaAsync(a.Token, dataset.Id, "fatura_no,tutar\nF-1,100\n");

        var response = await UploadAsync(dataset.Id, b.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(DisplayName = "Belge ucu: olmayan veri seti 404 döner")]
    public async Task OlmayanSet404()
    {
        var admin = await RegisterAsync("belge-404", "a@b404.com");

        var response = await UploadAsync(Guid.NewGuid(), admin.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(DisplayName = "Belge ucu: şemasız veri setinde çıkarım yapılmaz (400)")]
    public async Task SemasizSetReddedilir()
    {
        var admin = await RegisterAsync("belge-sema", "a@bsema.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Semasiz");

        var response = await UploadAsync(dataset.Id, admin.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("şema", await response.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "Belge ucu: görüntü olmayan dosya reddedilir (400)")]
    public async Task GorseldisiDosyaReddedilir()
    {
        var admin = await RegisterAsync("belge-uzanti", "a@buzanti.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Faturalar");
        await SetSchemaAsync(admin.Token, dataset.Id, "fatura_no,tutar\nF-1,100\n");

        var response = await UploadAsync(dataset.Id, admin.Token,
            Encoding.UTF8.GetBytes("a,b\n1,2\n"), "tablo.csv", "text/csv");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Belge ucu: boş dosya reddedilir (400)")]
    public async Task BosDosyaReddedilir()
    {
        var admin = await RegisterAsync("belge-bos", "a@bbos.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Faturalar");
        await SetSchemaAsync(admin.Token, dataset.Id, "fatura_no,tutar\nF-1,100\n");

        var response = await UploadAsync(dataset.Id, admin.Token, Array.Empty<byte>());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- onay: ekrandaki tablonun kaydedilmesi ----
    //
    // Bu uç modele HİÇ dokunmuyor: kaydedilen şey modelin okuduğu değil, kullanıcının
    // onayladığı hâl. O yüzden testler de sahte görsel servise ihtiyaç duymuyor.

    private record ConfirmDto(Guid DatasetId, int SavedRows, int TotalRows);

    private Task<HttpResponseMessage> ConfirmAsync(Guid datasetId, string token,
        IEnumerable<string> columns, IEnumerable<string[]> rows) =>
        _client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/datasets/{datasetId}/document/confirm", token,
            new { columns, rows }));

    [Fact(DisplayName = "Onay: satırlar EKLENİR (içe aktarma gibi eskilerin üstüne yazmaz)")]
    public async Task OnaySatirlariEkler()
    {
        var admin = await RegisterAsync("belge-onay", "a@bonay.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Faturalar");
        await SetSchemaAsync(admin.Token, dataset.Id, "fatura_no,tutar\nF-0,1\n");

        var ilk = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar" },
            new[] { new[] { "F-1001", "1500.75" }, new[] { "F-1001", "250.00" } });

        ilk.EnsureSuccessStatusCode();
        var sonuc = (await ilk.Content.ReadFromJsonAsync<ConfirmDto>())!;
        Assert.Equal(2, sonuc.SavedRows);

        // İkinci belge birincisini SİLMEMELİ: her belge bir kayıttır.
        var ikinci = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar" },
            new[] { new[] { "F-1002", "90.00" } });

        ikinci.EnsureSuccessStatusCode();
        var toplam = (await ikinci.Content.ReadFromJsonAsync<ConfirmDto>())!;
        Assert.Equal(1, toplam.SavedRows);
        Assert.Equal(3, toplam.TotalRows);

        var rows = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/rows", admin.Token));
        var body = await rows.Content.ReadAsStringAsync();
        Assert.Contains("\"total\":3", body);
        Assert.Contains("F-1001", body);
        Assert.Contains("F-1002", body);
    }

    [Fact(DisplayName = "Onay: tek hücre bile uymuyorsa HİÇBİR satır yazılmaz")]
    public async Task UymayanHucreVarsaHicbiriYazilmaz()
    {
        var admin = await RegisterAsync("belge-onay-hata", "a@bonayh.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Faturalar");
        await SetSchemaAsync(admin.Token, dataset.Id, "fatura_no,tutar\nF-0,1\n");

        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar" },
            new[]
            {
                new[] { "F-1001", "1500.75" },   // geçerli
                new[] { "F-1002", "yok" },       // tutar sayı değil
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Hangi hücrenin uymadığı geri dönmeli: onay ekranı onu işaretleyecek.
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("tutar", problem);

        // Ve yarısı yazılmış bir belge bırakılmamalı.
        var rows = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/rows", admin.Token));
        Assert.Contains("\"total\":0", await rows.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "Onay: hücre sayısı kolon sayısıyla uyuşmazsa reddedilir (sessiz kayma olmasın)")]
    public async Task HucreSayisiUyusmazsaReddedilir()
    {
        var admin = await RegisterAsync("belge-onay-kayma", "a@bonayk.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Faturalar");
        await SetSchemaAsync(admin.Token, dataset.Id, "fatura_no,tutar\nF-0,1\n");

        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar" },
            new[] { new[] { "F-1001" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Onay: Viewer kaydedemez (403), başka firmanın setine yazılamaz (404)")]
    public async Task OnayYetkiVeIzolasyon()
    {
        var admin = await RegisterAsync("belge-onay-yetki", "admin@bonayy.com");
        var dataset = await CreateDatasetAsync(admin.Token, "Faturalar");
        await SetSchemaAsync(admin.Token, dataset.Id, "fatura_no,tutar\nF-0,1\n");

        var invite = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/users/invite",
            admin.Token, new { email = "izleyici@bonayy.com", role = "Viewer" }));
        var link = (await invite.Content.ReadFromJsonAsync<InviteDto>())!;
        (await _client.PostAsJsonAsync($"/api/invitations/{link.Token}/accept",
            new { password = "KendiSifrem123!" })).EnsureSuccessStatusCode();
        var login = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "izleyici@bonayy.com", password = "KendiSifrem123!" });
        var viewer = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;

        var izleyici = await ConfirmAsync(dataset.Id, viewer.Token,
            new[] { "fatura_no", "tutar" }, new[] { new[] { "F-1", "5" } });
        Assert.Equal(HttpStatusCode.Forbidden, izleyici.StatusCode);

        var baska = await RegisterAsync("belge-onay-b", "b@bonayy.com");
        var caprazi = await ConfirmAsync(dataset.Id, baska.Token,
            new[] { "fatura_no", "tutar" }, new[] { new[] { "F-1", "5" } });
        Assert.Equal(HttpStatusCode.NotFound, caprazi.StatusCode);
    }

    // ---- uçtan uca: görsel servis sahte, gerisi gerçek ----

    // Sabit bir çıkarım döndürür: bir hücre bilerek şemaya uymuyor (tutar = "yok").
    private sealed class FakeVisionService : IDocumentVisionService
    {
        public Task<DocumentExtractionResult> ExtractAsync(Stream image,
            IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default)
        {
            var document = new ExtractedDocument(
                new Dictionary<string, string?> { ["fatura_no"] = "F-1001" },
                new[]
                {
                    new Dictionary<string, string?> { ["urun"] = "Kalem", ["tutar"] = "1500.75" },
                    new Dictionary<string, string?> { ["urun"] = "Defter", ["tutar"] = "yok" },
                },
                new[] { "Model beklenen \"alanlar\" sarmalayıcısını atladı." });

            return Task.FromResult(new DocumentExtractionResult(
                document,
                DocumentExtractionParser.ToParsedTable(document),
                Model: "sahte-vl",
                PromptTokens: 1800,
                NumCtx: 4096,
                Suspect: false,
                LongEdge: 1200,
                Attempts: 1,
                DurationMs: 42,
                Warnings: new[] { "Model beklenen \"alanlar\" sarmalayıcısını atladı." }));
        }

        // Keşif geçişi bu testlerde kullanılmıyor; ayrı testlerin kendi sahtesi var.
        public Task<DocumentExtractionResult> DiscoverAsync(Stream image,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact(DisplayName = "Belge ucu: çıkarım önizleme döner — satır KAYDEDİLMEZ, uymayan hücre işaretlenir")]
    public async Task CikarimOnizlemeDonerVeKaydetmez()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddScoped<IDocumentVisionService, FakeVisionService>()));

        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = "belge-uctan", email = "a@buctan.com", password = "Sifre123!" });
        register.EnsureSuccessStatusCode();
        var admin = (await register.Content.ReadFromJsonAsync<TokenResponse>())!;

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        { Content = JsonContent.Create(new { name = "Faturalar", description = (string?)null }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);
        var created = (await (await client.SendAsync(create)).Content
            .ReadFromJsonAsync<DatasetRow>())!;

        var schemaContent = new MultipartFormDataContent();
        var schemaFile = new ByteArrayContent(
            Encoding.UTF8.GetBytes("fatura_no,urun,tutar\nF-1,Kalem,100\n"));
        schemaFile.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        schemaContent.Add(schemaFile, "file", "sema.csv");
        var schemaRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasets/{created.Id}/schema")
        { Content = schemaContent };
        schemaRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);
        (await client.SendAsync(schemaRequest)).EnsureSuccessStatusCode();

        var upload = new MultipartFormDataContent();
        var image = new ByteArrayContent(Jpeg());
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        upload.Add(image, "file", "fatura.jpg");
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasets/{created.Id}/document/extract")
        { Content = upload };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = (await response.Content.ReadFromJsonAsync<ExtractDto>())!;

        // Belge alanı her kalem satırına tekrarlanır → CSV'den gelenle aynı şekil.
        Assert.Equal(new[] { "fatura_no", "urun", "tutar" }, result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal("F-1001", row[0]));

        // Şemaya uymayan hücre ELENMEZ, işaretlenir: onay ekranı düzelttirecek.
        var error = Assert.Single(result.Errors);
        Assert.Equal("tutar", error.Column);
        Assert.Equal("yok", error.Value);
        Assert.Equal(2, result.Rows.Count);

        Assert.False(result.Suspect);
        Assert.NotEmpty(result.Warnings);

        // En önemlisi: hiçbir şey KAYDEDİLMEDİ.
        var rows = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{created.Id}/rows", admin.Token));
        var body = await rows.Content.ReadAsStringAsync();
        Assert.Contains("\"total\":0", body);
    }
}
