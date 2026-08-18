using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Onay ekranındaki KOLON EŞLEMESİ: belgeden çıkan kolonların hedef setin şemasına
// oturtulması ve oturmayanların ne olacağı.
//
// Bu testlerin sebebi ölçülmüş bir SESSİZ VERİ KAYBI: ValidateRows şemadan yürüdüğü için
// tabloda olup şemada olmayan kolonu hiç görmüyordu. Model hedef şema verilmesine rağmen
// "urun_adi" yerine "ürün / hizmet" yazdığında o kolon kaydedilmiyor, kullanıcı ise tek bir
// uyarı bile almadan "kaydedildi" mesajını görüyordu. Aşağıdaki testlerin çoğu bu yüzden
// "kaydedilmeli" değil "REDDEDİLMELİ" diyor: veri kaybı hata vermeden gerçekleşemez.
public class DocumentColumnMappingTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public DocumentColumnMappingTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ---- yanıt şekilleri ----

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record DatasetRow(Guid Id, string Name, string? Description, int RowCount,
        DateTime CreatedAt, DateTime? UpdatedAt);

    private record ConfirmDto(Guid DatasetId, int SavedRows, int TotalRows);

    private record MappingDto(string Discovered, string Target, bool TypeConflict);

    private record AlignmentDto(Guid DatasetId, string Name, List<ColumnSchema> TargetColumns,
        List<MappingDto> Mappings, List<string> MissingColumns, List<string> ExtraColumns);

    private record SchemaColumnDto(string Name, string Type, int Ordinal);

    private record SchemaDto(Guid DatasetId, List<SchemaColumnDto> Columns);

    private record JobDto(Guid Id, string Kind, string Status, Guid? DatasetId, string? FileName,
        string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt,
        DateTime? ConfirmedAt, JsonElement? Result);

    // ---- yardımcılar ----

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

    // Şema gerçek yoldan kuruluyor (CSV yükleyerek): testin şeması ile üretimin şeması aynı
    // katmandan çıksın.
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

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasets/{dataset.Id}/schema")
        { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();

        return dataset;
    }

    private Task<HttpResponseMessage> ConfirmAsync(Guid datasetId, string token,
        IEnumerable<string> columns, IEnumerable<string[]> rows,
        IEnumerable<string>? newColumns = null) =>
        _client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/datasets/{datasetId}/document/confirm", token,
            new { columns, rows, newColumns }));

    private Task<HttpResponseMessage> AlignAsync(Guid datasetId, string token,
        IEnumerable<string> columns, IEnumerable<string[]> rows) =>
        _client.SendAsync(WithToken(HttpMethod.Post,
            $"/api/datasets/{datasetId}/document/align", token, new { columns, rows }));

    private async Task<int> RowCountAsync(Guid datasetId, string token)
    {
        var rows = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{datasetId}/rows", token));
        rows.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await rows.Content.ReadAsStringAsync())
            .RootElement.GetProperty("total").GetInt32();
    }

    // ---- onay: eşleşmeyen kolon ----

    [Fact(DisplayName = "Onay: şemada karşılığı olmayan kolon SESSİZCE ATILMAZ, istek reddedilir")]
    public async Task EslesmeyenKolonReddedilir()
    {
        var admin = await RegisterAsync(_client, "eslesme-red", "a@esred.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,urun_adi,tutar\nF-0,Kalem,1\n");

        // Modelin gerçekte ürettiği ada yakın bir örnek: "ürün / hizmet" şemadaki
        // "urun_adi" ile ad üzerinden tutmuyor.
        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "ürün / hizmet", "tutar" },
            new[] { new[] { "F-1001", "Kalem", "15.00" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Hangi kolonun sığmadığı söylenmeli: kullanıcı onu eşleyecek ya da ekleyecek.
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("ürün / hizmet", problem);

        // Ve hiçbir şey yazılmamalı — eskiden bu istek 200 dönüp ürün adlarını düşürüyordu.
        Assert.Equal(0, await RowCountAsync(dataset.Id, admin.Token));
    }

    [Fact(DisplayName = "Onay: kullanıcının EŞLEDİĞİ kolon doğru alana yazılır")]
    public async Task EslenenKolonDogruAlanaYazilir()
    {
        var admin = await RegisterAsync(_client, "eslesme-map", "a@esmap.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,urun_adi,tutar\nF-0,Kalem,1\n");

        // Onay ekranı başlıkları eşlemeye göre çevirip gönderiyor: "ürün / hizmet" → "urun_adi".
        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "urun_adi", "tutar" },
            new[] { new[] { "F-1001", "Kalem", "15.00" } });

        response.EnsureSuccessStatusCode();

        var rows = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/rows", admin.Token));
        Assert.Contains("Kalem", await rows.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "Onay: aynı set kolonuna iki belge kolonu eşlenemez (biri sessizce ezerdi)")]
    public async Task MukerrerKolonReddedilir()
    {
        var admin = await RegisterAsync(_client, "eslesme-mukerrer", "a@esmuk.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,tutar\nF-0,1\n");

        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar", "tutar" },
            new[] { new[] { "F-1001", "15.00", "20.00" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await RowCountAsync(dataset.Id, admin.Token));
    }

    // ---- onay: yeni kolon ekleme ----

    [Fact(DisplayName = "Yeni kolon: sete eklenir, tipi DEĞERLERDEN algılanır, satırlar birlikte yazılır")]
    public async Task YeniKolonEklenirVeTipiAlgilanir()
    {
        var admin = await RegisterAsync(_client, "yeni-kolon", "a@ykolon.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,tutar\nF-0,1\n");

        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar", "kdv_orani" },
            new[]
            {
                new[] { "F-1001", "1500.75", "20" },
                new[] { "F-1001", "250.00", "10" },
            },
            newColumns: new[] { "kdv_orani" });

        response.EnsureSuccessStatusCode();
        var sonuc = (await response.Content.ReadFromJsonAsync<ConfirmDto>())!;
        Assert.Equal(2, sonuc.SavedRows);

        // Kolon şemaya girdi mi ve tipi değerlerden mi geldi?
        var schema = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/schema", admin.Token));
        var kolonlar = (await schema.Content.ReadFromJsonAsync<SchemaDto>())!.Columns;

        var eklenen = Assert.Single(kolonlar.Where(c => c.Name == "kdv_orani"));
        Assert.Equal("number", eklenen.Type);
        Assert.Equal(2, eklenen.Ordinal);   // var olan kolonların ardına eklendi

        // Ve değer gerçekten yazıldı (kolon açılıp satır boş kalmadı).
        var rows = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/rows", admin.Token));
        Assert.Contains("kdv_orani", await rows.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "Yeni kolon: hücre hatası varsa kolon da AÇILMAZ (ya hep ya hiç)")]
    public async Task HucreHatasindaKolonAcilmaz()
    {
        var admin = await RegisterAsync(_client, "yeni-kolon-atomik", "a@ykatom.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,tutar\nF-0,1\n");

        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar", "kdv_orani" },
            new[] { new[] { "F-1001", "sayı değil", "20" } },
            newColumns: new[] { "kdv_orani" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Satır yazılmadıysa kolon da kalmamalı: yarım kalmış bir şema değişikliği,
        // kullanıcının hiç istemediği bir kalıcı iz bırakırdı.
        var schema = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{dataset.Id}/schema", admin.Token));
        var kolonlar = (await schema.Content.ReadFromJsonAsync<SchemaDto>())!.Columns;
        Assert.DoesNotContain(kolonlar, c => c.Name == "kdv_orani");
    }

    [Fact(DisplayName = "Yeni kolon: sette zaten var olan ad eklenemez (eşlemesi gerekirdi)")]
    public async Task VarOlanAdEklenemez()
    {
        var admin = await RegisterAsync(_client, "yeni-kolon-var", "a@ykvar.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,tutar\nF-0,1\n");

        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar" },
            new[] { new[] { "F-1001", "15.00" } },
            newColumns: new[] { "tutar" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("zaten var", await response.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "Yeni kolon: gönderilen tabloda bulunmayan kolon eklenemez")]
    public async Task TabloDisiKolonEklenemez()
    {
        var admin = await RegisterAsync(_client, "yeni-kolon-hayalet", "a@ykhayal.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,tutar\nF-0,1\n");

        var response = await ConfirmAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar" },
            new[] { new[] { "F-1001", "15.00" } },
            newColumns: new[] { "aciklama" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- hizalama ucu ----

    [Fact(DisplayName = "Hizalama: eşleşen, eksik ve fazla kolonlar ayrı ayrı döner")]
    public async Task HizalamaUcuEslemeyiDondurur()
    {
        var admin = await RegisterAsync(_client, "hizala", "a@hizala.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,fatura_tarihi,urun_adi,tutar\nF-0,2026-01-01,Kalem,1\n");

        var response = await AlignAsync(dataset.Id, admin.Token,
            new[] { "fatura no", "tarih", "ürün / hizmet", "tutar" },
            new[] { new[] { "F-1001", "2026-08-01", "Kalem", "15.00" } });

        response.EnsureSuccessStatusCode();
        var alignment = (await response.Content.ReadFromJsonAsync<AlignmentDto>())!;

        Assert.Equal("Faturalar", alignment.Name);
        Assert.Equal(4, alignment.TargetColumns.Count);

        // "fatura no" → "fatura_no" (ayraç farkı), "tarih" → "fatura_tarihi" (nitelenmiş ad),
        // "tutar" → "tutar". "ürün / hizmet" ise tutmuyor: kullanıcı elle eşleyecek.
        Assert.Contains(alignment.Mappings, m => m.Discovered == "fatura no" && m.Target == "fatura_no");
        Assert.Contains(alignment.Mappings, m => m.Discovered == "tarih" && m.Target == "fatura_tarihi");
        Assert.Equal(new[] { "ürün / hizmet" }, alignment.ExtraColumns);
        Assert.Equal(new[] { "urun_adi" }, alignment.MissingColumns);
    }

    [Fact(DisplayName = "Hizalama: tip uyuşmazlığı işaretlenir (belgede metin, sette sayı)")]
    public async Task HizalamaTipCakismasiniIsaretler()
    {
        var admin = await RegisterAsync(_client, "hizala-tip", "a@hiztip.com");
        var dataset = await CreateDatasetAsync(_client, admin.Token, "Faturalar",
            "fatura_no,tutar\nF-0,1\n");

        var response = await AlignAsync(dataset.Id, admin.Token,
            new[] { "fatura_no", "tutar" },
            new[] { new[] { "F-1001", "bin beş yüz" } });

        response.EnsureSuccessStatusCode();
        var alignment = (await response.Content.ReadFromJsonAsync<AlignmentDto>())!;

        var tutar = Assert.Single(alignment.Mappings.Where(m => m.Target == "tutar"));
        Assert.True(tutar.TypeConflict);
    }

    [Fact(DisplayName = "Hizalama: başka firmanın seti 404, şemasız set 400")]
    public async Task HizalamaKapiDavranisi()
    {
        var a = await RegisterAsync(_client, "hizala-a", "a@hizkapi.com");
        var b = await RegisterAsync(_client, "hizala-b", "b@hizkapi.com");

        var dataset = await CreateDatasetAsync(_client, a.Token, "Faturalar",
            "fatura_no,tutar\nF-0,1\n");

        var capraz = await AlignAsync(dataset.Id, b.Token,
            new[] { "fatura_no" }, new[] { new[] { "F-1" } });
        Assert.Equal(HttpStatusCode.NotFound, capraz.StatusCode);

        var semasiz = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets",
            a.Token, new { name = "Semasiz", description = (string?)null }));
        var bos = (await semasiz.Content.ReadFromJsonAsync<DatasetRow>())!;

        var response = await AlignAsync(bos.Id, a.Token,
            new[] { "fatura_no" }, new[] { new[] { "F-1" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- çıkarım sonucuna eklenen hizalama ----

    // Şemalı geçişte modelin hedef şemayı TAKİP ETMEDİĞİ gerçek durumu taklit eder:
    // sette "urun_adi" var, model "ürün / hizmet" yazıyor; ayrıca sette hiç olmayan
    // "logo" kolonu üretiyor.
    private sealed class SapanVisionService : IDocumentVisionService
    {
        public Task<DocumentExtractionResult> ExtractAsync(Stream image,
            IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default)
        {
            var document = new ExtractedDocument(
                new Dictionary<string, string?> { ["fatura_no"] = "F-1001", ["logo"] = "ACME" },
                new[]
                {
                    new Dictionary<string, string?>
                        { ["ürün / hizmet"] = "Kalem", ["tutar"] = "1500.75" },
                },
                Array.Empty<string>());

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
                Warnings: Array.Empty<string>()));
        }

        public Task<DocumentExtractionResult> DiscoverAsync(Stream image,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact(DisplayName = "Çıkarım: şema verilse bile model sapabilir — sonuç hizalamayı TAŞIR")]
    public async Task CikarimSonucuHizalamaTasir()
    {
        // Türetilmiş fabrika BİLEREK dispose edilmiyor: onu erken kapatmak sınıfın
        // paylaştığı sunucuyu da düşürüyor ve aynı sınıftaki sonraki testler 500 alıyor.
        // Temizliği ana fabrika yapıyor (türettiklerini kendisi izliyor ve kapatıyor).
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddScoped<IDocumentVisionService, SapanVisionService>()));

        var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "hizalama-cikarim", "a@hizcik.com");
        var dataset = await CreateDatasetAsync(client, admin.Token, "Faturalar",
            "fatura_no,urun_adi,tutar\nF-0,Kalem,1\n");

        var upload = new MultipartFormDataContent();
        var image = new ByteArrayContent(Jpeg());
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        upload.Add(image, "file", "fatura.jpg");

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasets/{dataset.Id}/document/extract")
        { Content = upload };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin.Token);

        var queued = (await (await client.SendAsync(request)).Content
            .ReadFromJsonAsync<JobDto>())!;

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDocumentJobRunner>()
                .RunAsync(queued.Id);

        var read = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/jobs/{queued.Id}", admin.Token));
        var job = (await read.Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal("succeeded", job.Status);

        var alignment = job.Result!.Value.GetProperty("alignment")
            .Deserialize<AlignmentDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // Tutan kolonlar eşleşti, sapanlar işaretlendi. Kullanıcı ekranda tam olarak bunu
        // görüyor: eskiden "ürün / hizmet" ile "logo" hiç sorulmadan düşüyordu.
        Assert.Contains(alignment.Mappings, m => m.Target == "fatura_no");
        Assert.Contains(alignment.Mappings, m => m.Target == "tutar");
        Assert.Contains("ürün / hizmet", alignment.ExtraColumns);
        Assert.Contains("logo", alignment.ExtraColumns);
        Assert.Contains("urun_adi", alignment.MissingColumns);
    }

    private static byte[] Jpeg(int width = 800, int height = 600)
    {
        using var image = new Image<Rgb24>(width, height);
        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer, new JpegEncoder { Quality = 90 });
        return buffer.ToArray();
    }
}
