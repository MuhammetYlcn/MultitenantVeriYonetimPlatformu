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

// KEŞİF ucu: belge şemasız okunur, hangi veri setine ait olabileceği önerilir.
//
// Ölçülen şey modelin okuma doğruluğu değil (o iş tools/belge_ureteci'nde), ucun kendisi:
// yetki, izolasyon, taslak şemanın gerçekten değerlerden tiplenmesi, önerinin doğru
// kurulması ve en önemlisi hiçbir şeyin KAYDEDİLMEMESİ.
public class DocumentDiscoveryTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;

    public DocumentDiscoveryTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record DatasetRow(Guid Id, string Name, string? Description, int RowCount,
        DateTime CreatedAt, DateTime? UpdatedAt);

    private record InviteDto(string Token, string Email, string? Role);

    private record MappingDto(string Discovered, string Target, bool TypeConflict);

    private record MatchDto(Guid DatasetId, string Name, double Score,
        List<MappingDto> Mappings, List<string> MissingColumns, List<string> ExtraColumns);

    private record DiscoverDto(
        string? DocumentType,
        List<ColumnSchema> Columns,
        List<string[]> Rows,
        List<MatchDto> Matches,
        string SuggestedName,
        List<string> Warnings,
        bool Suspect,
        string Model,
        int PromptTokens,
        int NumCtx,
        int LongEdge,
        int Attempts,
        int DurationMs);

    // Keşif geçişini taklit eder: bir fatura okumuş gibi, adları KENDİ seçmiş gibi döner.
    private sealed class FakeDiscoveryService : IDocumentVisionService
    {
        public Task<DocumentExtractionResult> ExtractAsync(Stream image,
            IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DocumentExtractionResult> DiscoverAsync(Stream image,
            CancellationToken ct = default)
        {
            var document = new ExtractedDocument(
                new Dictionary<string, string?>
                {
                    ["fatura_no"] = "F-1001",
                    ["fatura_tarihi"] = "2026-08-01",
                },
                new[]
                {
                    new Dictionary<string, string?> { ["urun_adi"] = "Kalem", ["tutar"] = "1500.75" },
                    new Dictionary<string, string?> { ["urun_adi"] = "Defter", ["tutar"] = "250.00" },
                },
                Array.Empty<string>(),
                DocumentType: "fatura");

            return Task.FromResult(new DocumentExtractionResult(
                document,
                DocumentExtractionParser.ToParsedTable(document),
                Model: "sahte-vl",
                PromptTokens: 1700,
                NumCtx: 4096,
                Suspect: false,
                LongEdge: 1200,
                Attempts: 1,
                DurationMs: 40,
                Warnings: Array.Empty<string>()));
        }
    }

    // ---- yardımcılar ----

    private static byte[] Jpeg(int width = 800, int height = 600)
    {
        using var image = new Image<Rgb24>(width, height);
        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer, new JpegEncoder { Quality = 90 });
        return buffer.ToArray();
    }

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage DiscoverRequest(string? token,
        byte[]? bytes = null, string fileName = "fatura.jpg", string contentType = "image/jpeg")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes ?? Jpeg());
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents/discover")
        { Content = content };

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    private static async Task<TokenResponse> RegisterAsync(HttpClient client, string name, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = name, email, password = "Sifre123!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<DatasetRow> CreateDatasetAsync(HttpClient client, string token,
        string name, string csv)
    {
        var create = await client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets", token,
            new { name, description = (string?)null }));
        create.EnsureSuccessStatusCode();
        var dataset = (await create.Content.ReadFromJsonAsync<DatasetRow>())!;

        // Şema gerçek yoldan kuruluyor (CSV yükleyerek): testin şeması ile üretimin şeması
        // aynı katmandan çıksın.
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "sema.csv");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/datasets/{dataset.Id}/schema")
        { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();

        return dataset;
    }

    private HttpClient FakeModelClient() => _factory
        .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVisionService, FakeDiscoveryService>()))
        .CreateClient();

    // ---- kapı testleri ----

    [Fact(DisplayName = "Keşif ucu: kimliksiz istek reddedilir (401)")]
    public async Task KimliksizIstekReddedilir()
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(DiscoverRequest(token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "Keşif ucu: Viewer belge okutamaz (403)")]
    public async Task ViewerOkutamaz()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "kesif-rol", "admin@krol.com");

        var invite = await client.SendAsync(WithToken(HttpMethod.Post, "/api/users/invite",
            admin.Token, new { email = "izleyici@krol.com", role = "Viewer" }));
        invite.EnsureSuccessStatusCode();
        var link = (await invite.Content.ReadFromJsonAsync<InviteDto>())!;

        (await client.PostAsJsonAsync($"/api/invitations/{link.Token}/accept",
            new { password = "KendiSifrem123!" })).EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "izleyici@krol.com", password = "KendiSifrem123!" });
        var viewer = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;

        var response = await client.SendAsync(DiscoverRequest(viewer.Token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(DisplayName = "Keşif ucu: görüntü olmayan dosya reddedilir (400)")]
    public async Task GorseldisiDosyaReddedilir()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "kesif-uzanti", "a@kuzanti.com");

        var response = await client.SendAsync(DiscoverRequest(admin.Token,
            Encoding.UTF8.GetBytes("a,b\n1,2\n"), "tablo.csv", "text/csv"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- uçtan uca: görsel servis sahte, gerisi gerçek ----

    [Fact(DisplayName = "Keşif ucu: taslak şema döner — TİPLER değerlerden algılanır, modelden değil")]
    public async Task TaslakSemaDegerlerdenTiplenir()
    {
        using var client = FakeModelClient();
        var admin = await RegisterAsync(client, "kesif-taslak", "a@ktaslak.com");

        var response = await client.SendAsync(DiscoverRequest(admin.Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = (await response.Content.ReadFromJsonAsync<DiscoverDto>())!;

        Assert.Equal("fatura", result.DocumentType);
        Assert.Equal(new[] { "fatura_no", "fatura_tarihi", "urun_adi", "tutar" },
            result.Columns.Select(c => c.Name));

        // Model bu tipleri söylemedi; hücrelere bakan ortak katman karar verdi.
        Assert.Equal("text", result.Columns[0].Type);
        Assert.Equal("date", result.Columns[1].Type);
        Assert.Equal("number", result.Columns[3].Type);

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact(DisplayName = "Keşif ucu: uyan veri seti bulunursa eşleme ayrıntısıyla önerilir")]
    public async Task UyanSetOnerilir()
    {
        using var client = FakeModelClient();
        var admin = await RegisterAsync(client, "kesif-oneri", "a@koneri.com");

        // Kolon adları belgedekiyle birebir aynı değil (tarih ve ürün nitelenmiş yazılmış):
        // eşleme yazım farkına dayanmalı.
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "Fatura No,Fatura Tarihi,Ürün Adı,Tutar\nF-1,2026-01-05,Kalem,100\n");

        var response = await client.SendAsync(DiscoverRequest(admin.Token));
        var result = (await response.Content.ReadFromJsonAsync<DiscoverDto>())!;

        var match = Assert.Single(result.Matches);
        Assert.Equal(dataset.Id, match.DatasetId);
        Assert.Equal("Faturalar", match.Name);
        Assert.Equal(1.0, match.Score);
        Assert.Equal(4, match.Mappings.Count);
        Assert.Empty(match.MissingColumns);
        Assert.Empty(match.ExtraColumns);

        // Eşleme kullanıcıya "hangi kolon nereye" diye gösterilecek; adlar taşınmalı.
        Assert.Contains(match.Mappings,
            m => m.Discovered == "fatura_tarihi" && m.Target == "Fatura Tarihi");
    }

    [Fact(DisplayName = "Keşif ucu: alakasız setler önerilmez — yeni set adı belgenin türünden gelir")]
    public async Task AlakasizSetOnerilmezYeniSetOnerilir()
    {
        using var client = FakeModelClient();
        var admin = await RegisterAsync(client, "kesif-yeni", "a@kyeni.com");

        await CreateDatasetAsync(client, admin.Token, "Personel",
            "ad,soyad,departman,maas\nAli,Yılmaz,Satış,50000\n");

        var response = await client.SendAsync(DiscoverRequest(admin.Token));
        var result = (await response.Content.ReadFromJsonAsync<DiscoverDto>())!;

        Assert.Empty(result.Matches);
        Assert.Equal("Fatura", result.SuggestedName);
    }

    [Fact(DisplayName = "Keşif ucu: BAŞKA FİRMANIN uyan veri seti asla önerilmez")]
    public async Task BaskaFirmaninSetiOnerilmez()
    {
        using var client = FakeModelClient();

        var baska = await RegisterAsync(client, "kesif-b", "b@kizole.com");
        await CreateDatasetAsync(client, baska.Token, "Faturalar",
            "fatura_no,fatura_tarihi,urun_adi,tutar\nF-1,2026-01-05,Kalem,100\n");

        var admin = await RegisterAsync(client, "kesif-a", "a@kizole.com");

        var response = await client.SendAsync(DiscoverRequest(admin.Token));
        var result = (await response.Content.ReadFromJsonAsync<DiscoverDto>())!;

        // Tam uyan bir set VAR ama başka firmanın; görünmemeli.
        Assert.Empty(result.Matches);
    }

    [Fact(DisplayName = "Keşif ucu: hiçbir şey KAYDEDİLMEZ — ne satır ne yeni set")]
    public async Task HicbirSeyKaydedilmez()
    {
        using var client = FakeModelClient();
        var admin = await RegisterAsync(client, "kesif-kayit", "a@kkayit.com");

        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,fatura_tarihi,urun_adi,tutar\nF-1,2026-01-05,Kalem,100\n");

        (await client.SendAsync(DiscoverRequest(admin.Token))).EnsureSuccessStatusCode();

        var rows = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/rows", admin.Token));
        Assert.Contains("\"total\":0", await rows.Content.ReadAsStringAsync());

        // Öneri "yeni set" bile olsa uç kendi kendine set AÇMAZ; o karar onay ekranında.
        var datasets = await client.SendAsync(
            WithToken(HttpMethod.Get, "/api/datasets", admin.Token));
        var list = (await datasets.Content.ReadFromJsonAsync<List<DatasetRow>>())!;
        Assert.Single(list);
    }
}
