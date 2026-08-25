using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// İZLEYİCİLER: kaydedilmiş sorgu + eşik + uyarı.
//
// Buradaki testlerin ağırlık merkezi tek bir soruda: izleyici YANLIŞ bir şey söylüyor mu?
// Bir alarmın en tehlikeli hâli çalışmaması değil, çalışmadığı hâlde çalışıyormuş gibi
// görünmesidir — kullanıcı ona güvenerek beklemeye başlar. Bu yüzden testlerin yarısı
// "sessizce sıfır dönmüyor", "aynı uyarıyı tekrar tekrar göndermiyor", "başka firmanın
// izleyicisini görmüyor" gibi olumsuz iddiaları sınıyor.
public class WatchTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;

    public WatchTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record DatasetRow(Guid Id, string Name, string? Description, int RowCount,
        DateTime CreatedAt, DateTime? UpdatedAt);

    private record InviteDto(string Token, string Email, string? Role);

    private record WatchDto(Guid Id, string Title, string Question, string Status, bool IsEnabled,
        int IntervalMinutes, string ConditionKind, string Op, decimal Threshold,
        decimal? LastValue, decimal? PreviousValue, DateTime? LastRunAt, DateTime? LastTriggeredAt,
        DateTime NextRunAt, string? Error, string CreatedBy, int UnreadCount);

    private record RunDto(Guid Id, DateTime RanAt, decimal? Value, bool Breached, string? Error,
        bool Notified, DateTime? ReadAt);

    private record DetailDto(WatchDto Watch, string Summary, List<RunDto> Runs);

    private record AlertDto(Guid RunId, Guid WatchId, string Title, DateTime RanAt,
        decimal? Value, string? Error, bool Broken);

    // ---- plan örnekleri ----
    //
    // Gerçekte bunları dil modeli üretiyor. Testte elle yazılmaları bilinçli: sınanan şey
    // modelin doğru plan üretmesi değil (o Adım 11'in konusu), kaydedilmiş bir planın
    // tekrar tekrar aynı şeyi ölçmesi.

    private const string ToplamTutarPlani =
        """{"kind":"aggregate","from":"Satislar","metrics":[{"op":"sum","column":"tutar"}]}""";

    private const string SehreGorePlan =
        """{"kind":"aggregate","from":"Satislar","groupBy":["sehir"],"metrics":[{"op":"sum","column":"tutar"}]}""";

    private const string DonemKarsilastirmaPlani =
        """{"kind":"aggregate","from":"Satislar","metrics":[{"op":"sum","column":"tutar"}],"compare":{"column":"tarih","period":"thisMonth","previous":"lastMonth"}}""";

    private const string SatirListesiPlani =
        """{"kind":"rows","from":"Satislar","select":["urun"],"limit":2}""";

    private const string BosKumeOrtalamaPlani =
        """{"kind":"aggregate","from":"Satislar","metrics":[{"op":"avg","column":"tutar"}],"filters":[{"column":"urun","op":"eq","value":"OlmayanUrun"}]}""";

    private const string BosKumeToplamPlani =
        """{"kind":"aggregate","from":"Satislar","metrics":[{"op":"sum","column":"tutar"}],"filters":[{"column":"urun","op":"eq","value":"OlmayanUrun"}]}""";

    private const string Csv = "urun,tutar,sehir\nKalem,100,Ankara\nDefter,250,Izmir\n";

    // ---- sahte planlayıcı ----
    //
    // Yalnız TEK bir testte kullanılıyor: /api/ask'in ürettiği planı gerçekten sakladığını
    // göstermek için. Ollama'ya bağımlı olmayalım diye plan sabit.
    private sealed class FakePlanner : IQueryPlannerService
    {
        public Task<PlanResult> PlanAsync(string question, TenantCatalog catalog,
            string? model = null, CancellationToken ct = default) =>
            Task.FromResult(new PlanResult(
                QueryPlanJson.Parse(ToplamTutarPlani)!, ToplamTutarPlani, 5, "sahte-planlayici"));

        public Task<IReadOnlyList<OllamaModel>> ListModelsAsync(bool includeHidden = false,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OllamaModel>>(Array.Empty<OllamaModel>());

        public Task<string> CompleteJsonAsync(string prompt, string? model = null,
            CancellationToken ct = default) => Task.FromResult("{}");
    }

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

    /// Şemalı ve satırlı bir veri seti kurar; izleyicinin ölçeceği sayı buradan çıkar.
    private static async Task<DatasetRow> CreateDatasetAsync(HttpClient client, string token,
        string name = "Satislar", string csv = Csv)
    {
        var create = await client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets", token,
            new { name, description = (string?)null }));
        create.EnsureSuccessStatusCode();
        var dataset = (await create.Content.ReadFromJsonAsync<DatasetRow>())!;

        await UploadCsvAsync(client, token, $"/api/datasets/{dataset.Id}/schema", csv);
        await UploadCsvAsync(client, token, $"/api/datasets/{dataset.Id}/rows", csv);

        return dataset;
    }

    private static async Task UploadCsvAsync(HttpClient client, string token, string url, string csv)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "veri.csv");

        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    /// Bir soru-cevap turunu doğrudan veritabanına yazar.
    ///
    /// /api/ask üzerinden gitmek gerçek modeli çağırmak olurdu; sınanan şey ise izleyici,
    /// planlayıcı değil. Planın /api/ask tarafından GERÇEKTEN saklandığı ayrı bir testte
    /// sahte planlayıcıyla gösteriliyor.
    private async Task<Guid> SeedMessageAsync(Guid userId, string question, string planJson)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = new AskConversation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = question
        };

        var message = new AskMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Question = question,
            ResponseJson = "{}",
            PlanJson = planJson
        };

        db.AskConversations.Add(conversation);
        db.AskMessages.Add(message);
        await db.SaveChangesAsync();

        return message.Id;
    }

    private static async Task<HttpResponseMessage> CreateWatchAsync(HttpClient client, string token,
        Guid messageId, string op = "gt", decimal threshold = 1000m,
        string kind = "value", int interval = 60) =>
        await client.SendAsync(WithToken(HttpMethod.Post, "/api/watches", token, new
        {
            messageId,
            intervalMinutes = interval,
            conditionKind = kind,
            op,
            threshold
        }));

    private static async Task<WatchDto> RunNowAsync(HttpClient client, string token, Guid watchId)
    {
        var response = await client.SendAsync(
            WithToken(HttpMethod.Post, $"/api/watches/{watchId}/run", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WatchDto>())!;
    }

    private static async Task<List<AlertDto>> AlertsAsync(HttpClient client, string token)
    {
        var response = await client.SendAsync(WithToken(HttpMethod.Get, "/api/watches/alerts", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<AlertDto>>())!;
    }

    private static async Task<TokenResponse> InviteViewerAsync(HttpClient client, string adminToken,
        string email)
    {
        var invite = await client.SendAsync(WithToken(HttpMethod.Post, "/api/users/invite",
            adminToken, new { email, role = "Viewer" }));
        invite.EnsureSuccessStatusCode();
        var link = (await invite.Content.ReadFromJsonAsync<InviteDto>())!;

        (await client.PostAsJsonAsync($"/api/invitations/{link.Token}/accept",
            new { password = "KendiSifrem123!" })).EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email, password = "KendiSifrem123!" });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    // ---- kurma ----

    [Fact(DisplayName = "İzleyici kurulurken plan bir kez ÇALIŞTIRILIR: doğrulanmış doğar")]
    public async Task KurulurkenOlculur()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-kur", "a@izlekur.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);

        var response = await CreateWatchAsync(client, admin.Token, messageId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var watch = (await response.Content.ReadFromJsonAsync<WatchDto>())!;

        // 100 + 250. İzleyici kurulur kurulmaz ölçtü.
        Assert.Equal(350m, watch.LastValue);
        Assert.Equal(WatchStatus.Ok, watch.Status);
        Assert.NotNull(watch.LastRunAt);
        // Kurulduğu an eşiğin altında olduğu için uyarı doğmadı.
        Assert.Equal(0, watch.UnreadCount);
    }

    [Fact(DisplayName = "İlk ölçüm geçmişe yazılır: grafiğin ilk noktası kuruluş anıdır")]
    public async Task IlkOlcumGecmiseYazilir()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-gecmis", "a@izlegecmis.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        var detail = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/watches/{created.Id}", admin.Token));
        detail.EnsureSuccessStatusCode();
        var body = (await detail.Content.ReadFromJsonAsync<DetailDto>())!;

        var run = Assert.Single(body.Runs);
        Assert.Equal(350m, run.Value);
        Assert.False(run.Notified);
        Assert.NotEmpty(body.Summary);
    }

    [Fact(DisplayName = "/api/ask planı saklar: verilen cevap doğrudan izlemeye alınabilir")]
    public async Task AskPlaniSaklar()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddScoped<IQueryPlannerService, FakePlanner>()));

        using var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-ask", "a@izleask.com");
        await CreateDatasetAsync(client, admin.Token);

        var ask = await client.SendAsync(WithToken(HttpMethod.Post, "/api/ask", admin.Token,
            new { question = "toplam tutar nedir" }));
        ask.EnsureSuccessStatusCode();

        var conversationId = (await ask.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversationId").GetGuid();

        Guid messageId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var message = await db.AskMessages.IgnoreQueryFilters()
                .FirstAsync(m => m.ConversationId == conversationId);

            // Asıl iddia: planın kendisi saklandı. Saklanmasaydı izleyici, ekranda cevabı
            // gösterilen sorgunun aynısını çalıştırdığını ispat edemezdi.
            Assert.False(string.IsNullOrWhiteSpace(message.PlanJson));
            messageId = message.Id;
        }

        var watch = await CreateWatchAsync(client, admin.Token, messageId);
        Assert.Equal(HttpStatusCode.Created, watch.StatusCode);
    }

    [Fact(DisplayName = "/api/ask yanıtı izleme kimliğini taşır: istemci veritabanına bakmaz")]
    public async Task AskYanitiIzlemeKimligiTasir()
    {
        // Arayüzdeki "İzle" düğmesinin dayanağı bu: yanıtın kendisi hangi cevabın
        // izleneceğini söylemiyorsa, istemcinin izleyici kurmasının yolu yok.
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddScoped<IQueryPlannerService, FakePlanner>()));

        using var client = factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-kimlik", "a@izlekimlik.com");
        await CreateDatasetAsync(client, admin.Token);

        var ask = await client.SendAsync(WithToken(HttpMethod.Post, "/api/ask", admin.Token,
            new { question = "toplam tutar nedir" }));
        ask.EnsureSuccessStatusCode();

        var body = await ask.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = body.GetProperty("messageId").GetGuid();

        // İzlenebilir bir plan: sebep alanı BOŞ olmalı, yoksa arayüz düğmeyi kapatır.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("watchBlockReason").ValueKind);

        var watch = await CreateWatchAsync(client, admin.Token, messageId);
        Assert.Equal(HttpStatusCode.Created, watch.StatusCode);
    }

    [Fact(DisplayName = "Geçmiş sohbette izlenemeyen cevap SEBEBİYLE gelir")]
    public async Task GecmisSohbetIzlenemeSebebiniTasir()
    {
        // Sebep okuma anında hesaplanıyor, kaydedilmiş yanıtın içinden okunmuyor: yanıt
        // verildiği gün donmuş bir kayıt, izlenebilirlik ise bugünün sorusu.
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-gecmis", "a@izlegecmis.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "şehirlere göre tutar", SehreGorePlan);

        var list = await client.SendAsync(
            WithToken(HttpMethod.Get, "/api/ask/conversations", admin.Token));
        list.EnsureSuccessStatusCode();

        var conversationId = (await list.Content.ReadFromJsonAsync<JsonElement>())[0]
            .GetProperty("id").GetGuid();

        var detail = await client.SendAsync(WithToken(HttpMethod.Get,
            $"/api/ask/conversations/{conversationId}", admin.Token));
        detail.EnsureSuccessStatusCode();

        var turn = (await detail.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("turns")[0];

        Assert.Equal(messageId, turn.GetProperty("messageId").GetGuid());
        // Gruplanmış plan izlenemez ve arayüz bunu düğmeyi gizleyerek değil, sebebi
        // göstererek anlatıyor — sebebin yanıtla birlikte gelmesinin tek sebebi bu.
        Assert.False(string.IsNullOrWhiteSpace(turn.GetProperty("watchBlockReason").GetString()));
    }

    // ---- izlenemeyen planlar ----

    [Fact(DisplayName = "Gruplanmış sonuç izlenemez: eşik tek sayıyla karşılaştırılır")]
    public async Task GruplanmisSonucIzlenemez()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-grup", "a@izlegrup.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "şehirlere göre tutar", SehreGorePlan);

        var response = await CreateWatchAsync(client, admin.Token, messageId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Dönem karşılaştırması izlenemez: iki dönem iki sayı demektir")]
    public async Task DonemKarsilastirmasiIzlenemez()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-donem", "a@izledonem.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "bu ay geçen aya göre",
            DonemKarsilastirmaPlani);

        var response = await CreateWatchAsync(client, admin.Token, messageId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Satır listesi SAYISIYLA izlenir; plandaki limit sayıyı kırpmaz")]
    public async Task SatirListesiSayiylaIzlenir()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-satir", "a@izlesatir.com");
        // Dört satır; plandaki limit 2. İzlenen sayı 4 olmalı: limit "kaç tanesini göster"
        // demektir, "kaç tane var" değil.
        await CreateDatasetAsync(client, admin.Token, csv:
            "urun,tutar,sehir\nA,10,Ankara\nB,20,Ankara\nC,30,Izmir\nD,40,Izmir\n");

        var messageId = await SeedMessageAsync(admin.UserId, "ürünleri listele", SatirListesiPlani);

        var response = await CreateWatchAsync(client, admin.Token, messageId);
        response.EnsureSuccessStatusCode();
        var watch = (await response.Content.ReadFromJsonAsync<WatchDto>())!;

        Assert.Equal(4m, watch.LastValue);
    }

    // ---- eşik ----

    [Fact(DisplayName = "Eşik aşılınca uyarı doğar ve izleyici 'aşıldı' durumuna geçer")]
    public async Task EsikAsilincaUyariDogar()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-esik", "a@izleesik.com");
        var dataset = await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        // 350 < 1000: kurulduğunda eşik aşılmamış.
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            "gt", 1000m)).Content.ReadFromJsonAsync<WatchDto>())!;
        Assert.Equal(WatchStatus.Ok, created.Status);

        // Satır ekleyip toplamı eşiğin üstüne çıkar.
        await client.SendAsync(WithToken(HttpMethod.Post, $"/api/datasets/{dataset.Id}/rows/add",
            admin.Token, new { values = new Dictionary<string, string?>
                { ["urun"] = "Masa", ["tutar"] = "5000", ["sehir"] = "Ankara" } }));

        var after = await RunNowAsync(client, admin.Token, created.Id);

        Assert.Equal(WatchStatus.Breaching, after.Status);
        Assert.Equal(5350m, after.LastValue);
        Assert.Equal(350m, after.PreviousValue);
        Assert.NotNull(after.LastTriggeredAt);
        Assert.Equal(1, after.UnreadCount);

        var alert = Assert.Single(await AlertsAsync(client, admin.Token));
        Assert.Equal(created.Id, alert.WatchId);
        Assert.Equal(5350m, alert.Value);
        Assert.False(alert.Broken);
    }

    [Fact(DisplayName = "Eşik aşılı KALINCA ikinci uyarı doğmaz: bildirim kenar tetiklemeli")]
    public async Task AsiliKalincaTekrarUyarmaz()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-kenar", "a@izlekenar.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            "gt", 1000m)).Content.ReadFromJsonAsync<WatchDto>())!;

        // Eşiği ölçülen değerin altına indir: bir sonraki koşuda geçiş olur.
        await client.SendAsync(WithToken(HttpMethod.Patch, $"/api/watches/{created.Id}",
            admin.Token, new { threshold = 100m }));

        // PATCH durumu zaten yeniden değerlendiriyor ve uyarı ÜRETMİYOR (kullanıcı ekranda).
        Assert.Empty(await AlertsAsync(client, admin.Token));

        await RunNowAsync(client, admin.Token, created.Id);
        var ikinci = await RunNowAsync(client, admin.Token, created.Id);

        Assert.Equal(WatchStatus.Breaching, ikinci.Status);
        // İki koşu da eşiğin üstünde bitti ama tek bir uyarı bile doğmadı: durum değişmedi.
        Assert.Empty(await AlertsAsync(client, admin.Token));
    }

    [Fact(DisplayName = "Eşik aşılmazsa uyarı doğmaz")]
    public async Task EsikAsilmazsaSessizKalir()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-sessiz", "a@izlesessiz.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            "gt", 1000m)).Content.ReadFromJsonAsync<WatchDto>())!;

        var after = await RunNowAsync(client, admin.Token, created.Id);

        Assert.Equal(WatchStatus.Ok, after.Status);
        Assert.Equal(0, after.UnreadCount);
        Assert.Empty(await AlertsAsync(client, admin.Token));
    }

    [Fact(DisplayName = "Değişim eşiği: ilk koşuda uyarı yok, artış eşiği aşınca uyarı doğar")]
    public async Task DegisimEsigi()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-degisim", "a@izledegisim.com");
        var dataset = await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);

        // "Önceki koşuya göre %20'den fazla artarsa haber ver."
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            "gt", 20m, kind: WatchConditionKind.Change)).Content.ReadFromJsonAsync<WatchDto>())!;

        // İlk ölçümde karşılaştırılacak önceki değer yok: taban kaydedildi, uyarı yok.
        Assert.Equal(WatchStatus.Ok, created.Status);
        Assert.Equal(350m, created.LastValue);

        // 350 → 700: %100 artış.
        await client.SendAsync(WithToken(HttpMethod.Post, $"/api/datasets/{dataset.Id}/rows/add",
            admin.Token, new { values = new Dictionary<string, string?>
                { ["urun"] = "Sandalye", ["tutar"] = "350", ["sehir"] = "Izmir" } }));

        var after = await RunNowAsync(client, admin.Token, created.Id);

        Assert.Equal(WatchStatus.Breaching, after.Status);
        Assert.Equal(700m, after.LastValue);
        Assert.Single(await AlertsAsync(client, admin.Token));
    }

    // ---- boş küme ----

    [Fact(DisplayName = "Boş kümede TOPLAM sıfırdır: 'satış yok' hâli izleyiciyi susturmaz")]
    public async Task BosKumedeToplamSifir()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-bostoplam", "a@izlebostoplam.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar", BosKumeToplamPlani);

        var response = await CreateWatchAsync(client, admin.Token, messageId, "lt", 10m);
        response.EnsureSuccessStatusCode();
        var watch = (await response.Content.ReadFromJsonAsync<WatchDto>())!;

        // Hiç eşleşen satır yok → toplam 0 → "10'un altına düştü" koşulu sağlanır.
        Assert.Equal(0m, watch.LastValue);
        Assert.Equal(WatchStatus.Breaching, watch.Status);
    }

    [Fact(DisplayName = "Boş kümede ORTALAMA tanımsızdır: sıfır uydurulmaz, değer boş kalır")]
    public async Task BosKumedeOrtalamaTanimsiz()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-bosort", "a@izlebosort.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "ortalama tutar", BosKumeOrtalamaPlani);

        var response = await CreateWatchAsync(client, admin.Token, messageId, "lt", 10m);
        response.EnsureSuccessStatusCode();
        var watch = (await response.Content.ReadFromJsonAsync<WatchDto>())!;

        // Ortalaması olmayan bir küme için "ortalama 0'a düştü" demek, olmayan bir olguyu
        // bildirmek olurdu.
        Assert.Null(watch.LastValue);
        Assert.Equal(WatchStatus.Ok, watch.Status);
    }

    // ---- kırık izleyici ----

    [Fact(DisplayName = "Dayandığı veri seti silinen izleyici KIRIK olur, sessizce sıfır dönmez")]
    public async Task KirikIzleyiciSessizKalmaz()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-kirik", "a@izlekirik.com");
        var dataset = await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            "lt", 1000m)).Content.ReadFromJsonAsync<WatchDto>())!;

        // 350 < 1000 → kurulduğunda eşik zaten aşılmış durumda.
        Assert.Equal(WatchStatus.Breaching, created.Status);

        // Planın dayandığı veri seti gidiyor.
        (await client.SendAsync(WithToken(HttpMethod.Delete, $"/api/datasets/{dataset.Id}",
            admin.Token))).EnsureSuccessStatusCode();

        var after = await RunNowAsync(client, admin.Token, created.Id);

        Assert.Equal(WatchStatus.Broken, after.Status);
        Assert.False(string.IsNullOrWhiteSpace(after.Error));
        // En son bilinen değer korunuyor: "ne zaman ne görmüştük" sorusu kırıldıktan sonra
        // da sorulabilmeli.
        Assert.Equal(350m, after.LastValue);
        Assert.Equal(1, after.UnreadCount);

        var alert = Assert.Single(await AlertsAsync(client, admin.Token));
        Assert.True(alert.Broken);
        // ASIL İDDİA: kırık koşu SIFIR değil BOŞ değer kaydeder. Sıfır kaydedilseydi
        // grafikte gerçek bir düşüş gibi görünür, kullanıcı çalışmayan alarma güvenirdi.
        Assert.Null(alert.Value);
    }

    [Fact(DisplayName = "Kırık izleyici her koşuda tekrar tekrar uyarmaz")]
    public async Task KirikIzleyiciTekrarUyarmaz()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-kirik2", "a@izlekirik2.com");
        var dataset = await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        (await client.SendAsync(WithToken(HttpMethod.Delete, $"/api/datasets/{dataset.Id}",
            admin.Token))).EnsureSuccessStatusCode();

        await RunNowAsync(client, admin.Token, created.Id);
        await RunNowAsync(client, admin.Token, created.Id);

        Assert.Single(await AlertsAsync(client, admin.Token));
    }

    // ---- bildirim kutusu ----

    [Fact(DisplayName = "Okundu işaretleme rozeti sıfırlar")]
    public async Task OkunduIsaretleme()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-okundu", "a@izleokundu.com");
        var dataset = await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            "gt", 1000m)).Content.ReadFromJsonAsync<WatchDto>())!;

        await client.SendAsync(WithToken(HttpMethod.Post, $"/api/datasets/{dataset.Id}/rows/add",
            admin.Token, new { values = new Dictionary<string, string?>
                { ["urun"] = "Masa", ["tutar"] = "5000", ["sehir"] = "Ankara" } }));

        await RunNowAsync(client, admin.Token, created.Id);
        Assert.Single(await AlertsAsync(client, admin.Token));

        var read = await client.SendAsync(WithToken(HttpMethod.Post, "/api/watches/alerts/read",
            admin.Token, new { runIds = (Guid[]?)null }));
        read.EnsureSuccessStatusCode();

        Assert.Empty(await AlertsAsync(client, admin.Token));
    }

    // ---- izolasyon ----

    [Fact(DisplayName = "İzolasyon: başka firmanın izleyicisi ne listede ne de tek tek görünür")]
    public async Task BaskaFirmaGoremez()
    {
        using var client = _factory.CreateClient();
        var a = await RegisterAsync(client, "izle-izole-a", "a@izleizole.com");
        await CreateDatasetAsync(client, a.Token);

        var messageId = await SeedMessageAsync(a.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, a.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        var b = await RegisterAsync(client, "izle-izole-b", "b@izleizole.com");

        var list = await client.SendAsync(WithToken(HttpMethod.Get, "/api/watches", b.Token));
        list.EnsureSuccessStatusCode();
        Assert.Empty((await list.Content.ReadFromJsonAsync<List<WatchDto>>())!);

        var detail = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/watches/{created.Id}", b.Token));
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        var delete = await client.SendAsync(
            WithToken(HttpMethod.Delete, $"/api/watches/{created.Id}", b.Token));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        Assert.Empty(await AlertsAsync(client, b.Token));
    }

    [Fact(DisplayName = "Başkasının sohbetindeki cevap izlemeye alınamaz")]
    public async Task BaskasininCevabiIzlenemez()
    {
        using var client = _factory.CreateClient();
        var a = await RegisterAsync(client, "izle-sohbet-a", "a@izlesohbet.com");
        await CreateDatasetAsync(client, a.Token);
        var messageId = await SeedMessageAsync(a.UserId, "toplam tutar nedir", ToplamTutarPlani);

        var b = await RegisterAsync(client, "izle-sohbet-b", "b@izlesohbet.com");

        var response = await CreateWatchAsync(client, b.Token, messageId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(DisplayName = "İzleyici FİRMAYA aittir: aynı firmadaki başka kullanıcı da görür")]
    public async Task AyniFirmadakiBaskasiGorur()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-paylasim", "a@izlepaylasim.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        (await CreateWatchAsync(client, admin.Token, messageId)).EnsureSuccessStatusCode();

        var viewer = await InviteViewerAsync(client, admin.Token, "izleyici@izlepaylasim.com");

        var list = await client.SendAsync(WithToken(HttpMethod.Get, "/api/watches", viewer.Token));
        list.EnsureSuccessStatusCode();

        // Sohbetin aksine izleyici kişisel değil: alarmı kuran kişi izinliyken de görülmeli.
        Assert.Single((await list.Content.ReadFromJsonAsync<List<WatchDto>>())!);
    }

    // ---- roller ----

    [Fact(DisplayName = "Viewer izleyici kuramaz (403) ama listeyi görebilir")]
    public async Task ViewerKuramazAmaGorebilir()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-rol", "a@izlerol.com");
        await CreateDatasetAsync(client, admin.Token);

        var viewer = await InviteViewerAsync(client, admin.Token, "izleyici@izlerol.com");
        var messageId = await SeedMessageAsync(viewer.UserId, "toplam tutar nedir", ToplamTutarPlani);

        var create = await CreateWatchAsync(client, viewer.Token, messageId);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var list = await client.SendAsync(WithToken(HttpMethod.Get, "/api/watches", viewer.Token));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact(DisplayName = "Viewer izleyiciyi silemez ve elle çalıştıramaz (403)")]
    public async Task ViewerDegistiremez()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-rol2", "a@izlerol2.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        var viewer = await InviteViewerAsync(client, admin.Token, "izleyici@izlerol2.com");

        var delete = await client.SendAsync(
            WithToken(HttpMethod.Delete, $"/api/watches/{created.Id}", viewer.Token));
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        var run = await client.SendAsync(
            WithToken(HttpMethod.Post, $"/api/watches/{created.Id}/run", viewer.Token));
        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
    }

    // ---- zamanlanmış koşu ----
    //
    // Buradaki testlerin sınadığı asıl şey kuyruk değil TENANT BAĞLAMI: arka planda HTTP
    // isteği yok, yani firma kimliği token'dan okunamıyor. Kurulmazsa bütün query
    // filter'lar "TenantId == null" hâline düşer ve izleyici hiçbir veri göremez —
    // ya da daha kötüsü, yanlış kurulursa başka firmanın verisini ölçer.

    /// İzleyicinin koşu zamanını geçmişe çeker: tarama onu "süresi gelmiş" sayar.
    private async Task MakeDueAsync(Guid watchId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var watch = await db.DatasetWatches.IgnoreQueryFilters().FirstAsync(w => w.Id == watchId);
        watch.NextRunAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private async Task SweepAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWatchScheduler>().SweepAsync();
    }

    private async Task<WatchDto> ReadWatchAsync(HttpClient client, string token, Guid watchId)
    {
        var response = await client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/watches/{watchId}", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DetailDto>())!.Watch;
    }

    [Fact(DisplayName = "Tarama: süresi gelen izleyici koşar, gelmeyen dokunulmaz")]
    public async Task TaramaSuresiGeleniKosturur()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-tarama", "a@izletarama.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var gelen = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;
        var gelmeyen = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        // Karşılaştırma veritabanından okunan iki değer arasında yapılıyor: yanıt gövdesinde
        // dönen zaman bellekteki hâliyle (100 ns çözünürlük), veritabanından okunan ise
        // mikrosaniyeye yuvarlanmış olarak gelir; ikisini doğrudan kıyaslamak testi
        // ölçmediği bir şeye bağlardı.
        var gelenOnce = await ReadWatchAsync(client, admin.Token, gelen.Id);
        var gelmeyenOnce = await ReadWatchAsync(client, admin.Token, gelmeyen.Id);

        await MakeDueAsync(gelen.Id);
        await SweepAsync();

        var kosan = await ReadWatchAsync(client, admin.Token, gelen.Id);
        var kosmayan = await ReadWatchAsync(client, admin.Token, gelmeyen.Id);

        Assert.True(kosan.LastRunAt > gelenOnce.LastRunAt);
        Assert.Equal(gelmeyenOnce.LastRunAt, kosmayan.LastRunAt);
    }

    [Fact(DisplayName = "Tarama arka planda İZOLASYONU korur: her izleyici kendi firmasını ölçer")]
    public async Task TaramaIzolasyonuKorur()
    {
        using var client = _factory.CreateClient();

        // İki firma, AYNI ADLI veri seti, farklı toplamlar. Bağlam yanlış kurulsaydı
        // ikisinden biri diğerinin sayısını ölçerdi ve hiçbir hata görünmezdi.
        var a = await RegisterAsync(client, "izle-tizole-a", "a@izletizole.com");
        await CreateDatasetAsync(client, a.Token, csv: "urun,tutar,sehir\nA,100,Ankara\n");
        var aMessage = await SeedMessageAsync(a.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var aWatch = (await (await CreateWatchAsync(client, a.Token, aMessage))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        var b = await RegisterAsync(client, "izle-tizole-b", "b@izletizole.com");
        await CreateDatasetAsync(client, b.Token, csv: "urun,tutar,sehir\nB,900,Izmir\n");
        var bMessage = await SeedMessageAsync(b.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var bWatch = (await (await CreateWatchAsync(client, b.Token, bMessage))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        await MakeDueAsync(aWatch.Id);
        await MakeDueAsync(bWatch.Id);
        await SweepAsync();

        Assert.Equal(100m, (await ReadWatchAsync(client, a.Token, aWatch.Id)).LastValue);
        Assert.Equal(900m, (await ReadWatchAsync(client, b.Token, bWatch.Id)).LastValue);
    }

    [Fact(DisplayName = "Tarama: kapatılmış izleyici koşmaz ama silinmez")]
    public async Task TaramaKapaliIzleyiciyiAtlar()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-kapali", "a@izlekapali.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        (await client.SendAsync(WithToken(HttpMethod.Patch, $"/api/watches/{created.Id}",
            admin.Token, new { isEnabled = false }))).EnsureSuccessStatusCode();

        var before = await ReadWatchAsync(client, admin.Token, created.Id);

        await MakeDueAsync(created.Id);
        await SweepAsync();

        var after = await ReadWatchAsync(client, admin.Token, created.Id);

        Assert.False(after.IsEnabled);
        Assert.Equal(before.LastRunAt, after.LastRunAt);
    }

    // ---- doğrulama ----

    [Fact(DisplayName = "Serbest koşu sıklığı reddedilir: yalnız sabit liste kabul edilir")]
    public async Task SerbestSiklikReddedilir()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-siklik", "a@izlesiklik.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);

        var response = await CreateWatchAsync(client, admin.Token, messageId, interval: 1);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Planı olmayan eski cevap izlenemez ve sebebi söylenir")]
    public async Task PlansizCevapIzlenemez()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-plansiz", "a@izleplansiz.com");
        await CreateDatasetAsync(client, admin.Token);

        Guid messageId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversation = new AskConversation
            {
                Id = Guid.NewGuid(), UserId = admin.UserId, Title = "eski"
            };
            var message = new AskMessage
            {
                Id = Guid.NewGuid(), ConversationId = conversation.Id,
                Question = "eski soru", ResponseJson = "{}", PlanJson = null
            };
            db.AskConversations.Add(conversation);
            db.AskMessages.Add(message);
            await db.SaveChangesAsync();
            messageId = message.Id;
        }

        var response = await CreateWatchAsync(client, admin.Token, messageId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Sıklık değişince sıradaki koşu yeniden hesaplanır")]
    public async Task SiklikDegisinceSiradakiKosuKayar()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-siklik2", "a@izlesiklik2.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            interval: 1440)).Content.ReadFromJsonAsync<WatchDto>())!;

        var patch = await client.SendAsync(WithToken(HttpMethod.Patch, $"/api/watches/{created.Id}",
            admin.Token, new { intervalMinutes = 15 }));
        patch.EnsureSuccessStatusCode();
        var updated = (await patch.Content.ReadFromJsonAsync<WatchDto>())!;

        Assert.Equal(15, updated.IntervalMinutes);
        // Günlükten 15 dakikaya çekilen izleyici yine bir gün beklememeli.
        Assert.True(updated.NextRunAt < created.NextRunAt);
    }

    // ---- e-posta ----
    //
    // Uyarının uygulama DIŞINA çıkması. Buradaki testlerin ortak iddiası tek: e-posta bir
    // KOLAYLIK, doğruluk kaynağı değil. Gidememesi, yanlış kişiye gitmesi ya da hiç
    // ayarlanmamış olması uyarının kendisini bozmamalı.

    /// Gerçek SMTP yerine geçen sahte gönderici. Sınanan şey MailKit'in postayı taşıması
    /// değil (o kütüphanenin işi), uygulamanın DOĞRU mesajı DOĞRU adreslere vermesi.
    private sealed class FakeEmailSender : IEmailSender
    {
        private readonly List<EmailMessage> _sent = new();

        public bool IsEnabled { get; init; } = true;

        /// Gönderim sırasında patlasın mı — "kanal düşerse ne olur" testi için.
        public bool Throws { get; init; }

        public IReadOnlyList<EmailMessage> Sent
        {
            get { lock (_sent) return _sent.ToList(); }
        }

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (Throws) throw new InvalidOperationException("SMTP sunucusuna ulaşılamadı.");

            lock (_sent) _sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private WebApplicationFactory<Program> FactoryWithEmail(FakeEmailSender sender) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(sender)));

    /// Eşiği aşan bir izleyici kurar ve onu koşturur: uyarı doğar.
    private async Task<WatchDto> TriggerBreachAsync(HttpClient client, TokenResponse admin,
        string tenantSlug)
    {
        var dataset = await CreateDatasetAsync(client, admin.Token);
        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);

        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            "gt", 1000m)).Content.ReadFromJsonAsync<WatchDto>())!;

        await client.SendAsync(WithToken(HttpMethod.Post, $"/api/datasets/{dataset.Id}/rows/add",
            admin.Token, new { values = new Dictionary<string, string?>
                { ["urun"] = $"Masa-{tenantSlug}", ["tutar"] = "5000", ["sehir"] = "Ankara" } }));

        return await RunNowAsync(client, admin.Token, created.Id);
    }

    [Fact(DisplayName = "Eşik aşılınca uyarı firmanın TÜM kullanıcılarına e-postayla gider")]
    public async Task UyariEpostayaDonusur()
    {
        var sender = new FakeEmailSender();
        using var factory = FactoryWithEmail(sender);
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "izle-posta", "admin@izleposta.com");
        // İkinci kullanıcı VIEWER: uyarı firmaya ait, rol ayrımı yapılmıyor. Yalnız
        // izleyiciyi kurana gönderilseydi, o kişi izinliyken alarm kimseye ulaşmazdı.
        await InviteViewerAsync(client, admin.Token, "izleyen@izleposta.com");

        var after = await TriggerBreachAsync(client, admin, "posta");
        Assert.Equal(WatchStatus.Breaching, after.Status);

        var mail = Assert.Single(sender.Sent);
        Assert.Contains("admin@izleposta.com", mail.To);
        Assert.Contains("izleyen@izleposta.com", mail.To);
        Assert.Contains(after.Title, mail.Subject);
        // Ölçülen sayı gövdede taşınıyor: kullanıcı sırf değeri görmek için uygulamayı
        // açmak zorunda kalmamalı.
        Assert.Contains("5.350", mail.Body);
        Assert.Contains("1.000", mail.Body);
    }

    [Fact(DisplayName = "E-posta yalnız kendi firmasının adreslerine gider")]
    public async Task EpostaFirmaDisinaCikmaz()
    {
        var sender = new FakeEmailSender();
        using var factory = FactoryWithEmail(sender);
        using var client = factory.CreateClient();

        // İki firma, aynı sunucu. Adresler firma bağlamından değil izleyicinin kendi
        // TenantId'sinden okunuyor; yanlış kurulsaydı bir firmanın alarmı diğerinin
        // posta kutusuna düşerdi — üstelik ölçülen sayıyı da yanında götürerek.
        var a = await RegisterAsync(client, "izle-posta-a", "a@izlepostaizole.com");
        await RegisterAsync(client, "izle-posta-b", "b@izlepostaizole.com");

        await TriggerBreachAsync(client, a, "a");

        var mail = Assert.Single(sender.Sent);
        Assert.Equal(new[] { "a@izlepostaizole.com" }, mail.To);
    }

    [Fact(DisplayName = "Kırık izleyici de e-posta doğurur ve konusu ayrıdır")]
    public async Task KirikIzleyiciEpostasi()
    {
        var sender = new FakeEmailSender();
        using var factory = FactoryWithEmail(sender);
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "izle-posta-kirik", "a@izlepostakirik.com");
        var dataset = await CreateDatasetAsync(client, admin.Token);
        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId,
            "gt", 1000m)).Content.ReadFromJsonAsync<WatchDto>())!;

        (await client.SendAsync(WithToken(HttpMethod.Delete, $"/api/datasets/{dataset.Id}",
            admin.Token))).EnsureSuccessStatusCode();

        await RunNowAsync(client, admin.Token, created.Id);

        var mail = Assert.Single(sender.Sent);
        // Konu satırı ayrı: kullanıcı posta kutusunu açmadan, "eşik aşıldı" ile
        // "alarm çalışmıyor" arasındaki farkı görebilmeli.
        Assert.Contains("çalışmıyor", mail.Subject);
        Assert.Contains("ÇALIŞMIYOR", mail.Body);
    }

    [Fact(DisplayName = "E-posta gönderilemezse uyarı KAYBOLMAZ, koşu da düşmez")]
    public async Task EpostaDuserseUyariKalir()
    {
        var sender = new FakeEmailSender { Throws = true };
        using var factory = FactoryWithEmail(sender);
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "izle-posta-hata", "a@izlepostahata.com");

        // Koşu isteği 200 dönüyor: SMTP sunucusunun ulaşılamaz olması bir VERİ sorunu
        // değil. Düşseydi izleyici "kırık" işaretlenir, kullanıcı verisinde olmayan bir
        // arızayı aramaya başlardı.
        var after = await TriggerBreachAsync(client, admin, "hata");

        Assert.Equal(WatchStatus.Breaching, after.Status);
        Assert.Equal(1, after.UnreadCount);

        // Asıl iddia: uyarı uygulamada duruyor. E-posta onun kopyasıydı, kendisi değil.
        var alert = Assert.Single(await AlertsAsync(client, admin.Token));
        Assert.Equal(5350m, alert.Value);
    }

    [Fact(DisplayName = "E-posta ayarı yoksa özellik kapalıdır: uyarı yine üretilir")]
    public async Task AyarYoksaOzellikKapali()
    {
        var sender = new FakeEmailSender { IsEnabled = false };
        using var factory = FactoryWithEmail(sender);
        using var client = factory.CreateClient();

        var admin = await RegisterAsync(client, "izle-posta-kapali", "a@izlepostakapali.com");

        var after = await TriggerBreachAsync(client, admin, "kapali");

        Assert.Equal(WatchStatus.Breaching, after.Status);
        Assert.Empty(sender.Sent);
        // Ayarsız bir kurulumda sistem tam olarak eskisi gibi çalışmaya devam ediyor.
        Assert.Single(await AlertsAsync(client, admin.Token));
    }

    // ---- koşu geçmişinin bakımı ----

    /// İzleyiciye elle koşu kaydı yazar. Gerçekte bunlar aylar içinde birikiyor; testin
    /// tavanı aşması için zamanı beklemek yerine geçmişe kayıt düşülüyor.
    private async Task SeedRunsAsync(Guid watchId, int count, bool oldestIsUnreadAlert = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var baseTime = DateTime.UtcNow.AddDays(-count);

        var runs = Enumerable.Range(0, count).Select(i => new DatasetWatchRun
        {
            Id = Guid.NewGuid(),
            WatchId = watchId,
            RanAt = baseTime.AddMinutes(i),
            Value = i,
            // En eski kayıt, kullanıcının HİÇ GÖRMEDİĞİ bir uyarı olabiliyor.
            Notified = oldestIsUnreadAlert && i == 0,
            Breached = oldestIsUnreadAlert && i == 0
        }).ToList();

        db.DatasetWatchRuns.AddRange(runs);
        await db.SaveChangesAsync();
    }

    private async Task CleanRunsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWatchRunCleaner>().CleanAsync();
    }

    private async Task<List<DatasetWatchRun>> ReadRunsAsync(Guid watchId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.DatasetWatchRuns.IgnoreQueryFilters()
            .Where(r => r.WatchId == watchId)
            .OrderBy(r => r.RanAt)
            .ToListAsync();
    }

    [Fact(DisplayName = "Bakım: koşu geçmişi sınırsız birikmez, en yeniler kalır")]
    public async Task KosuGecmisiBudanir()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-bakim", "a@izlebakim.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        // Kuruluş koşusuyla birlikte 561 kayıt: saatlik koşan bir izleyicide bu 23 gün.
        await SeedRunsAsync(created.Id, 560);
        Assert.Equal(561, (await ReadRunsAsync(created.Id)).Count);

        await CleanRunsAsync();

        var kalan = await ReadRunsAsync(created.Id);

        // Tavan 500 (bkz. WatchRunCleaner.KeepPerWatch).
        Assert.Equal(500, kalan.Count);

        // Silinenler EN ESKİLER: 561 kayıttan en eski 61'i gitti, grafiğin ucundaki
        // güncel noktalar duruyor. Sondaki kayıt izleyicinin kuruluş ölçümü (350).
        Assert.Equal(61m, kalan.First().Value);
        Assert.Equal(350m, kalan.Last().Value);

        // Bakım iki kez koşarsa ikincisi bir şey silmemeli: sınır bir kez uygulanır.
        await CleanRunsAsync();
        Assert.Equal(500, (await ReadRunsAsync(created.Id)).Count);
    }

    [Fact(DisplayName = "Bakım OKUNMAMIŞ uyarıyı silmez: görülmemiş alarm sessizce kaybolamaz")]
    public async Task BakimOkunmamisUyariyiSilmez()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-bakim2", "a@izlebakim2.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        // En eski kayıt, kullanıcının henüz görmediği bir uyarı — yani tam da bakımın
        // silmeye aday gördüğü yerde duruyor.
        await SeedRunsAsync(created.Id, 560, oldestIsUnreadAlert: true);

        var uyari = Assert.Single(await AlertsAsync(client, admin.Token));

        await CleanRunsAsync();

        var kalan = await ReadRunsAsync(created.Id);

        // 500 yeni + korunan uyarı. Rozet ve bildirim kutusu bu kaydı sayıyor; bakımın
        // onu düşürmesi, hiç görülmemiş bir alarmı yok saymak olurdu.
        Assert.Equal(501, kalan.Count);
        Assert.Contains(kalan, r => r.Id == uyari.RunId);
        Assert.Single(await AlertsAsync(client, admin.Token));
    }

    [Fact(DisplayName = "Bakım sınırın altındaki geçmişe dokunmaz")]
    public async Task BakimAzKayitliIzleyiciyeDokunmaz()
    {
        using var client = _factory.CreateClient();
        var admin = await RegisterAsync(client, "izle-bakim3", "a@izlebakim3.com");
        await CreateDatasetAsync(client, admin.Token);

        var messageId = await SeedMessageAsync(admin.UserId, "toplam tutar nedir", ToplamTutarPlani);
        var created = (await (await CreateWatchAsync(client, admin.Token, messageId))
            .Content.ReadFromJsonAsync<WatchDto>())!;

        await SeedRunsAsync(created.Id, 20);

        await CleanRunsAsync();

        // Normal hâl bu: bakım hiçbir şey silmeden dönüyor.
        Assert.Equal(21, (await ReadRunsAsync(created.Id)).Count);
    }
}
