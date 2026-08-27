using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// ASENKRON belge işleme: iş kaydının yaşam döngüsü, arka plandaki firma izolasyonu,
// hata yolu ve bakım.
//
// Buradaki testlerin çoğu tek bir soruyu farklı yerlerden soruyor: HTTP isteği yokken
// firma kimliği doğru kurulmuş mu? İzolasyonun tamamı o değere dayandığı için, arka plan
// işi bu projede yazılan en riskli parça.
public class DocumentJobTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;

    public DocumentJobTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record DatasetRow(Guid Id, string Name, string? Description, int RowCount,
        DateTime CreatedAt, DateTime? UpdatedAt);

    private record JobDto(Guid Id, string Kind, string Status, Guid? DatasetId, string? FileName,
        string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt,
        JsonElement? Result);

    private record ExtractDto(Guid DatasetId, List<string> Columns, List<string[]> Rows);

    // ---- sahte model gerçeklemeleri ----

    // Okuyabilen model: iki kalem satırı olan bir fatura döner.
    private sealed class FakeVisionService : IDocumentVisionService
    {
        public Task<DocumentExtractionResult> ExtractAsync(Stream image,
            IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default)
        {
            var document = new ExtractedDocument(
                new Dictionary<string, string?> { ["fatura_no"] = "F-1001" },
                new[]
                {
                    new Dictionary<string, string?> { ["urun"] = "Kalem", ["tutar"] = "100" },
                    new Dictionary<string, string?> { ["urun"] = "Defter", ["tutar"] = "250" },
                },
                Array.Empty<string>(),
                DocumentType: "fatura");

            return Task.FromResult(Result(document));
        }

        public Task<DocumentExtractionResult> DiscoverAsync(Stream image,
            CancellationToken ct = default) => ExtractAsync(image, Array.Empty<ColumnSchema>(), ct);

        private static DocumentExtractionResult Result(ExtractedDocument document) =>
            new(document, DocumentExtractionParser.ToParsedTable(document),
                Model: "sahte-vl", PromptTokens: 1500, NumCtx: 4096, Suspect: false,
                LongEdge: 1200, Attempts: 1, DurationMs: 30, Warnings: Array.Empty<string>());
    }

    // Model servisi kapalıymış gibi davranır.
    private sealed class UnavailableVisionService : IDocumentVisionService
    {
        public Task<DocumentExtractionResult> ExtractAsync(Stream image,
            IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default) =>
            throw new QueryPlannerException("Görsel model servisine ulaşılamadı. Ollama çalışıyor mu?");

        public Task<DocumentExtractionResult> DiscoverAsync(Stream image,
            CancellationToken ct = default) =>
            throw new QueryPlannerException("Görsel model servisine ulaşılamadı. Ollama çalışıyor mu?");
    }

    // Belgeyi okuyamamış model (bulanık fotoğraf) — kullanıcının düzeltebileceği hata.
    private sealed class UnreadableVisionService : IDocumentVisionService
    {
        public Task<DocumentExtractionResult> ExtractAsync(Stream image,
            IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default) =>
            throw new InvalidQueryException("Belge okunamadı; görüntü net değilse daha iyi bir çekim deneyin.");

        public Task<DocumentExtractionResult> DiscoverAsync(Stream image,
            CancellationToken ct = default) =>
            throw new InvalidQueryException("Belge okunamadı; görüntü net değilse daha iyi bir çekim deneyin.");
    }

    // ---- yardımcılar ----

    private WebApplicationFactory<Program> FactoryWith<TVision>()
        where TVision : class, IDocumentVisionService => _factory
        .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVisionService, TVision>()));

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

    private static async Task<TokenResponse> RegisterAsync(HttpClient client, string name,
        string email)
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

    private static async Task<JobDto> QueueExtractAsync(HttpClient client, string token,
        Guid datasetId)
    {
        var content = new MultipartFormDataContent();
        var image = new ByteArrayContent(Jpeg());
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(image, "file", "fatura.jpg");

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasets/{datasetId}/document/extract")
        { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JobDto>())!;
    }

    private static async Task RunAsync(WebApplicationFactory<Program> factory, Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDocumentJobRunner>().RunAsync(jobId);
    }

    private static async Task<JobDto> ReadJobAsync(HttpClient client, string token, Guid jobId)
    {
        var response = await client.SendAsync(WithToken(HttpMethod.Get, $"/api/jobs/{jobId}", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobDto>())!;
    }

    // ---- yaşam döngüsü ----

    [Fact(DisplayName = "İş kuyruğa alınır: uç 202 döner, sonuç HENÜZ yoktur")]
    public async Task IsKuyrugaAlinir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-kuyruk", "a@iskuyruk.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);

        Assert.Equal("extract", queued.Kind);
        Assert.Equal("queued", queued.Status);
        Assert.Equal(dataset.Id, queued.DatasetId);
        Assert.Equal("fatura.jpg", queued.FileName);

        // İstek dönerken model HENÜZ çağrılmadı: uzun işin isteğin dışında kalması,
        // bu adımın bütün gerekçesi.
        Assert.Null(queued.StartedAt);
        Assert.Null(queued.CompletedAt);
        Assert.Null(queued.Result);
    }

    [Fact(DisplayName = "İş çalışınca sonuç kayda yazılır ve durum 'succeeded' olur")]
    public async Task IsCalisincaSonucYazilir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-sonuc", "a@issonuc.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);
        await RunAsync(factory, queued.Id);

        var job = await ReadJobAsync(client, admin.Token, queued.Id);

        Assert.Equal("succeeded", job.Status);
        Assert.Null(job.Error);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.CompletedAt);

        var result = job.Result!.Value.Deserialize<ExtractDto>(JsonWeb)!;
        Assert.Equal(dataset.Id, result.DatasetId);
        Assert.Equal(new[] { "fatura_no", "urun", "tutar" }, result.Columns);
        Assert.Equal(2, result.Rows.Count);

        // Sonuç ÖNİZLEME: iş bitti diye satırlar kendiliğinden kaydedilmez.
        var rows = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/rows", admin.Token));
        Assert.Contains("\"total\":0", await rows.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "Aynı iş ikinci kez çalıştırılırsa atlanır — sonuç ezilmez")]
    public async Task IkinciCalistirmaAtlanir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-tekrar", "a@istekrar.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);
        await RunAsync(factory, queued.Id);

        var first = await ReadJobAsync(client, admin.Token, queued.Id);

        // İkinci çalıştırma: kuyruğa iki kez düşmüş ya da elle tetiklenmiş olabilir.
        // Kullanıcı onay ekranında sonuca bakarken tablonun altından çekilmesi kabul edilemez.
        await RunAsync(factory, queued.Id);

        var second = await ReadJobAsync(client, admin.Token, queued.Id);

        Assert.Equal(first.CompletedAt, second.CompletedAt);
        Assert.Equal(first.Result!.Value.ToString(), second.Result!.Value.ToString());
    }

    // ---- hata yolu ----

    [Fact(DisplayName = "Model servisi kapalıysa iş 'failed' olur ve mesaj kullanıcıya taşınır")]
    public async Task ModelKapaliysaIsDuser()
    {
        var factory = FactoryWith<UnavailableVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-kapali", "a@iskapali.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);
        await RunAsync(factory, queued.Id);

        var job = await ReadJobAsync(client, admin.Token, queued.Id);

        // Hata işi düşürmez, KAYDA yazılır: kullanıcı bekleyip sonunda ne olduğunu görmeli.
        Assert.Equal("failed", job.Status);
        Assert.Contains("ulaşılamadı", job.Error);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.Result);
    }

    [Fact(DisplayName = "Belge okunamazsa iş 'failed' olur — kullanıcıya düzeltilebilir mesaj döner")]
    public async Task BelgeOkunamazsaIsDuser()
    {
        var factory = FactoryWith<UnreadableVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-okunmaz", "a@isokunmaz.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);
        await RunAsync(factory, queued.Id);

        var job = await ReadJobAsync(client, admin.Token, queued.Id);

        Assert.Equal("failed", job.Status);
        Assert.Contains("okunamadı", job.Error);
    }

    [Fact(DisplayName = "Şemasız sette çıkarım kuyruğa BİLE alınmaz (400)")]
    public async Task SemasizSetKuyrugaAlinmaz()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-semasiz", "a@issemasiz.com");

        var create = await client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets",
            admin.Token, new { name = "Boş", description = (string?)null }));
        var dataset = (await create.Content.ReadFromJsonAsync<DatasetRow>())!;

        var content = new MultipartFormDataContent();
        var image = new ByteArrayContent(Jpeg());
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(image, "file", "fatura.jpg");

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasets/{dataset.Id}/document/extract")
        { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);

        var response = await client.SendAsync(request);

        // Denetim kuyruğa almadan ÖNCE: kullanıcı iki dakika bekleyip "şema yok" duymamalı.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.DocumentJobs.IgnoreQueryFilters().ToListAsync());
    }

    // ---- izolasyon ----

    [Fact(DisplayName = "Arka plan işi YALNIZ kendi firmasının verisini görür")]
    public async Task ArkaPlanIsiKendiFirmasiniGorur()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        // İki firma, ikisinde de AYNI şemaya sahip birer set var.
        var baska = await RegisterAsync(client, "is-izole-b", "b@isizole.com");
        var baskaSet = await CreateDatasetAsync(client, baska.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-9,Silgi,5\n");

        var admin = await RegisterAsync(client, "is-izole-a", "a@isizole.com");
        var kendiSet = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, kendiSet.Id);
        await RunAsync(factory, queued.Id);

        var job = await ReadJobAsync(client, admin.Token, queued.Id);
        Assert.Equal("succeeded", job.Status);

        var result = job.Result!.Value.Deserialize<ExtractDto>(JsonWeb)!;

        // İş, HTTP isteğinin dışında çalıştı; firma kimliği token'dan değil iş kaydından
        // kuruldu. Yanlış kurulsaydı sonuç ya boş kalır ya öteki firmanın setine bakardı.
        Assert.Equal(kendiSet.Id, result.DatasetId);
        Assert.NotEqual(baskaSet.Id, result.DatasetId);
    }

    [Fact(DisplayName = "Başka kullanıcının işi okunamaz (404) — iş kişiseldir")]
    public async Task BaskasininIsiOkunamaz()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var sahip = await RegisterAsync(client, "is-sahip", "a@issahip.com");
        var dataset = await CreateDatasetAsync(client, sahip.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        var queued = await QueueExtractAsync(client, sahip.Token, dataset.Id);

        var yabanci = await RegisterAsync(client, "is-yabanci", "b@isyabanci.com");

        var response = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/jobs/{queued.Id}", yabanci.Token));

        // 403 değil 404: kaydın var olduğu bilgisi bile sızdırılmıyor.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(DisplayName = "Liste yalnız kullanıcının kendi işlerini döndürür")]
    public async Task ListeKendiIslerinigosterir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var sahip = await RegisterAsync(client, "is-liste-a", "a@isliste.com");
        var dataset = await CreateDatasetAsync(client, sahip.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        await QueueExtractAsync(client, sahip.Token, dataset.Id);

        var yabanci = await RegisterAsync(client, "is-liste-b", "b@isliste.com");

        var mine = await client.SendAsync(WithToken(HttpMethod.Get, "/api/jobs", sahip.Token));
        var theirs = await client.SendAsync(WithToken(HttpMethod.Get, "/api/jobs", yabanci.Token));

        Assert.Single((await mine.Content.ReadFromJsonAsync<List<JobDto>>())!);
        Assert.Empty((await theirs.Content.ReadFromJsonAsync<List<JobDto>>())!);
    }

    [Fact(DisplayName = "Tenant bağlamı istek yolunda elle kurulamaz")]
    public async Task IstekYolundaBaglamKurulamaz()
    {
        // Arka plan bağlamını kuran yetenek yanlışlıkla bir controller'da kullanılırsa,
        // o istek başka firmanın verisine bakabilirdi. Kapı bu yüzden kapalı.
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "is-baglam", "a@isbaglam.com");

        var accessor = new HttpContextAccessorStub();
        var context = new TenantContext(accessor);

        Assert.Throws<InvalidOperationException>(
            () => context.SetForBackgroundWork(admin.TenantId));
    }

    [Fact(DisplayName = "Tenant bağlamı bir kapsamda ikinci kez değiştirilemez")]
    public async Task BaglamIkinciKezKurulamaz()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "is-baglam2", "a@isbaglam2.com");

        var context = new TenantContext(new HttpContextAccessorStub(withContext: false));
        context.SetForBackgroundWork(admin.TenantId);

        Assert.Equal(admin.TenantId, context.TenantId);
        Assert.Throws<InvalidOperationException>(() => context.SetForBackgroundWork(Guid.NewGuid()));
    }

    private sealed class HttpContextAccessorStub : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        public HttpContextAccessorStub(bool withContext = true) =>
            HttpContext = withContext ? new Microsoft.AspNetCore.Http.DefaultHttpContext() : null;

        public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
    }

    // ---- görüntü ----

    [Fact(DisplayName = "Belge görüntüsü saklanır ve onay ekranı için geri okunabilir")]
    public async Task GoruntuSaklanirVeOkunur()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-goruntu", "a@isgoruntu.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);

        var image = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/jobs/{queued.Id}/image", admin.Token));

        image.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await image.Content.ReadAsByteArrayAsync());
    }

    [Fact(DisplayName = "Onaydan sonra belge görüntüsü silinir (404)")]
    public async Task OnaydanSonraGoruntuSilinir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-onay", "a@isonay.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);
        await RunAsync(factory, queued.Id);

        var confirm = await client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/datasets/{dataset.Id}/document/confirm", admin.Token,
            new
            {
                columns = new[] { "fatura_no", "urun", "tutar" },
                rows = new[] { new[] { "F-1001", "Kalem", "100" } },
                jobId = queued.Id
            }));

        confirm.EnsureSuccessStatusCode();

        // Görüntü işin ömrüne bağlı bir ara üründü; satırlar yazıldığına göre işi bitti.
        var image = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/jobs/{queued.Id}/image", admin.Token));

        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
    }

    [Fact(DisplayName = "Aynı belge İKİNCİ kez kaydedilemez (409) — mükerrer satır oluşmaz")]
    public async Task IkinciOnayReddedilir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-mukerrer", "a@ismukerrer.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");

        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);
        await RunAsync(factory, queued.Id);

        object Body() => new
        {
            columns = new[] { "fatura_no", "urun", "tutar" },
            rows = new[] { new[] { "F-1001", "Kalem", "100" } },
            jobId = queued.Id
        };

        var first = await client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/datasets/{dataset.Id}/document/confirm", admin.Token, Body()));
        first.EnsureSuccessStatusCode();

        // İş listesi kalıcı olduğu için kullanıcı onayladığı belgeyi tekrar açabiliyor.
        // Ekranda düğme kapalı ama denetim SUNUCUDA olmak zorunda: arayüz doğruluğun
        // bekçisi olamaz.
        var second = await client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/datasets/{dataset.Id}/document/confirm", admin.Token, Body()));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Asıl ölçülen: satır sayısı ARTMADI.
        var rows = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/rows", admin.Token));
        Assert.Contains("\"total\":1", await rows.Content.ReadAsStringAsync());
    }

    // ---- atma ----

    [Fact(DisplayName = "Belge atılabilir: iş kaydı ve görüntüsü silinir")]
    public async Task BelgeAtilabilir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-at", "a@isat.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);

        // Yanlış belge yüklendiğinde kullanıcının elinde onu ortadan kaldıracak bir yol
        // olmalı; yoksa iş kalıcı olarak "kontrol bekliyor" durumunda kalır.
        var delete = await client.SendAsync(
            WithToken(HttpMethod.Delete, $"/api/jobs/{queued.Id}", admin.Token));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var read = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/jobs/{queued.Id}", admin.Token));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.DocumentJobs.IgnoreQueryFilters().ToListAsync());
    }

    [Fact(DisplayName = "Okuma sürerken de atılabilir — çalıştırıcı kaydı bulamayınca sessizce çıkar")]
    public async Task CalisirkenAtilabilir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-at2", "a@isat2.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);

        (await client.SendAsync(
            WithToken(HttpMethod.Delete, $"/api/jobs/{queued.Id}", admin.Token)))
            .EnsureSuccessStatusCode();

        // Kuyruktaki iş silinen kaydı çalıştırmaya kalkarsa düşmemeli.
        await RunAsync(factory, queued.Id);
    }

    [Fact(DisplayName = "Başkasının işi atılamaz (404)")]
    public async Task BaskasininIsiAtilamaz()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var sahip = await RegisterAsync(client, "is-at-sahip", "a@isatsahip.com");
        var dataset = await CreateDatasetAsync(client, sahip.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        var queued = await QueueExtractAsync(client, sahip.Token, dataset.Id);

        var yabanci = await RegisterAsync(client, "is-at-yabanci", "b@isatyabanci.com");

        var delete = await client.SendAsync(
            WithToken(HttpMethod.Delete, $"/api/jobs/{queued.Id}", yabanci.Token));

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // Kayıt duruyor: silme denemesi sahibini etkilemedi.
        var read = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/jobs/{queued.Id}", sahip.Token));
        read.EnsureSuccessStatusCode();
    }

    // ---- bakım ----

    [Fact(DisplayName = "Bakım: saatlerdir 'çalışıyor' görünen iş kapatılır")]
    public async Task AsiliIsKapatilir()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-asili", "a@isasili.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);

        // Sunucu işin ortasında yeniden başlatılmış gibi: kayıt "çalışıyor"da asılı kaldı.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.DocumentJobs.IgnoreQueryFilters()
                .FirstAsync(j => j.Id == queued.Id);

            job.Status = DocumentJobStatus.Running;
            job.StartedAt = DateTime.UtcNow.AddHours(-3);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDocumentJobCleaner>().CleanAsync();

        var cleaned = await ReadJobAsync(client, admin.Token, queued.Id);

        // Kullanıcı bitmeyecek bir işi süresiz beklememeli.
        Assert.Equal("failed", cleaned.Status);
        Assert.Contains("yarıda kaldı", cleaned.Error);
    }

    [Fact(DisplayName = "Bakım: KUYRUKTA asılı kalmış iş de kapatılır")]
    public async Task KuyruktaAsiliIsKapatilir()
    {
        // Kod incelemesinde bulunan kusur: bakım yalnız `running` işleri tarıyordu.
        //
        // İş kaydı ile Hangfire kuyruğu AYRI iki işlemde yazılıyor (önce kayıt, sonra
        // Enqueue). Enqueue düşerse (bağlantı kopması, sürecin tam o an kapanması) iş
        // hiçbir zaman çalışmıyor; `StartedAt` null olduğu için bakım onu asılı saymıyor
        // ve kayıt `queued` olarak kalıyordu. Kullanıcı ekranda sonu gelmeyen bir "sırada"
        // görüyor, kayıt 30 gün sonra sessizce siliniyordu.
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-kuyruk", "a@iskuyruk.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);

        // Kuyruğa hiç girememiş gibi: durum "sırada", oluşturulma zamanı çok eski.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.DocumentJobs.IgnoreQueryFilters()
                .FirstAsync(j => j.Id == queued.Id);

            job.CreatedAt = DateTime.UtcNow.AddHours(-5);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDocumentJobCleaner>().CleanAsync();

        var cleaned = await ReadJobAsync(client, admin.Token, queued.Id);

        Assert.Equal("failed", cleaned.Status);
        Assert.Contains("yarıda kaldı", cleaned.Error);
    }

    [Fact(DisplayName = "Bakım: asılı iş kapatılınca KULLANICIYA haber veriliyor")]
    public async Task AsiliIsKapatilinca_BildirimGider()
    {
        // Durumu Running'den Failed'a çeviren ikinci yer bakım işiydi ve oradan hiçbir
        // bildirim gitmiyordu. Sunucu işin ortasında yeniden başlatılıyor, bir süre sonra
        // bakım kaydı kapatıyor — ama ekrandaki kart SONSUZA KADAR "okunuyor" kalıyordu.
        var notifier = new FakeJobNotifier();

        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IDocumentVisionService, FakeVisionService>();
                services.AddScoped<IJobNotifier>(_ => notifier);
            }));

        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-bildirim", "a@isbildirim.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.DocumentJobs.IgnoreQueryFilters()
                .FirstAsync(j => j.Id == queued.Id);

            job.Status = DocumentJobStatus.Running;
            job.StartedAt = DateTime.UtcNow.AddHours(-3);
            await db.SaveChangesAsync();
        }

        notifier.Sent.Clear();

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDocumentJobCleaner>().CleanAsync();

        var bildirim = Assert.Single(notifier.Sent);
        Assert.Equal(queued.Id, bildirim.Id);
        Assert.Equal(DocumentJobStatus.Failed, bildirim.Status);
    }

    /// Bildirimleri toplayan sahte kanal: sınanan şey bildirimin ağdan geçmesi değil,
    /// doğru anlarda ve doğru durumla ÇAĞRILMASI.
    private sealed class FakeJobNotifier : IJobNotifier
    {
        public List<DocumentJob> Sent { get; } = new();

        public Task NotifyAsync(DocumentJob job, CancellationToken ct = default)
        {
            Sent.Add(job);
            return Task.CompletedTask;
        }
    }

    [Fact(DisplayName = "Bakım: onaylanmamış eski belgenin görüntüsü düşürülür")]
    public async Task EskiGoruntuDusurulur()
    {
        var factory = FactoryWith<FakeVisionService>();
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "is-eski", "a@iseski.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun,tutar\nF-1,Kalem,100\n");
        var queued = await QueueExtractAsync(client, admin.Token, dataset.Id);
        await RunAsync(factory, queued.Id);

        // İş günler önce bitmiş ama kullanıcı hiç onaylamamış.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.DocumentJobs.IgnoreQueryFilters()
                .FirstAsync(j => j.Id == queued.Id);

            job.CompletedAt = DateTime.UtcNow.AddDays(-5);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDocumentJobCleaner>().CleanAsync();

        var image = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/jobs/{queued.Id}/image", admin.Token));
        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);

        // Kayıt DURUYOR: silinen yalnız görüntü. Kullanıcı ne yaptığını geriye dönük görebilmeli.
        var job2 = await ReadJobAsync(client, admin.Token, queued.Id);
        Assert.Equal("succeeded", job2.Status);
    }
}
