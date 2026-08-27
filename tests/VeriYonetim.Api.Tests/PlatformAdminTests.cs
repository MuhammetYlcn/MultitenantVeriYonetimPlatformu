using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Xunit.Abstractions;

namespace VeriYonetim.Api.Tests;

/// <summary>
/// Platform yönetim katmanının testleri. En önemlileri "kanıt" testleridir:
/// platform yöneticisinin müşteri VERİSİNE erişemediğini gösterirler. Projenin
/// KVKK savunması ("veri kurum dışına çıkmaz, platformu işleten bile göremez")
/// buraya dayanır — sözlü bir iddia değil, çalışan bir kontrol.
/// </summary>
public class PlatformAdminTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public PlatformAdminTests(ApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _output = output;
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Yardımcılar ----

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record PlatformToken(Guid AdminId, string Email, string Token);

    private record TenantSummary(Guid Id, string Name, string Slug, bool IsActive,
        DateTime CreatedAt, DateTime? SuspendedAt, int UserCount, int DatasetCount, int RowCount);

    private record PlatformStats(int TenantCount, int ActiveTenantCount, int SuspendedTenantCount,
        int UserCount, int DatasetCount, int RowCount);

    private record AuditEntry(Guid Id, string PlatformAdminEmail, string Action,
        Guid? TargetTenantId, string? TargetTenantName, DateTime CreatedAt);

    private record DatasetRow(Guid Id, string Name, string? Description, int RowCount);

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<TokenResponse> RegisterTenantAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = name, email, password = "Sifre123!" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private async Task<PlatformToken> PlatformLoginAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/platform/auth/login",
            new { email = ApiFactory.PlatformAdminEmail, password = ApiFactory.PlatformAdminPassword });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlatformToken>())!;
    }

    // Bilinen bir değeri ("Ankara") içeren gerçek veri kurar — kanıt testinde bu
    // değerin platform yanıtlarında GEÇMEDİĞİNİ göstereceğiz.
    private async Task<Guid> SeedDataAsync(string tenantToken)
    {
        var created = await _client.SendAsync(WithToken(HttpMethod.Post, "/api/datasets",
            tenantToken, new { name = "Satislar", description = (string?)null }));
        created.EnsureSuccessStatusCode();
        var datasetId = (await created.Content.ReadFromJsonAsync<DatasetRow>())!.Id;

        const string csv = "ad,yas,sehir\nAli,30,Ankara\nAyse,25,Izmir\nVeli,40,Ankara";
        await UploadAsync(tenantToken, datasetId, "schema", csv);
        await UploadAsync(tenantToken, datasetId, "rows", csv);
        return datasetId;
    }

    private async Task UploadAsync(string token, Guid datasetId, string endpoint, string csv)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "test.csv");

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/datasets/{datasetId}/{endpoint}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    // ---- Giriş ----

    [Fact(DisplayName = "Platform: ayarlardan tohumlanan yönetici giriş yapabilir")]
    public async Task PlatformAdmin_CanLogin()
    {
        var platform = await PlatformLoginAsync();

        Assert.Equal(ApiFactory.PlatformAdminEmail, platform.Email);
        Assert.False(string.IsNullOrWhiteSpace(platform.Token));
    }

    [Fact(DisplayName = "Platform: hatalı şifre 401 döner")]
    public async Task PlatformAdmin_WrongPassword_Unauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/platform/auth/login",
            new { email = ApiFactory.PlatformAdminEmail, password = "YanlisSifre123!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(DisplayName = "Platform: yönetici oluşturmanın public ucu YOK")]
    public async Task PlatformAdmin_HasNoPublicRegistrationEndpoint()
    {
        // Kayıt ucu olsaydı isteyen herkes tüm firmaları yönetebilirdi. Kimlik
        // yalnızca sunucu ayarlarından tohumlanır.
        var response = await _client.PostAsJsonAsync("/api/platform/auth/register",
            new { email = "sizma@kotu.com", password = "Sifre123!" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        _output.WriteLine("✓ Kanıt: /api/platform/auth/register yok (404) — platform yöneticisi self-servis oluşturulamaz.");
    }

    // ---- KANIT: platform yöneticisi veriye erişemez ----

    [Theory(DisplayName = "KANIT: platform token'ı müşteri verisi uçlarına giremez (403)")]
    [InlineData("/api/datasets")]
    [InlineData("/api/users")]
    [InlineData("/api/auth/me")]
    public async Task PlatformToken_CannotAccessTenantEndpoints(string url)
    {
        await RegisterTenantAsync("kanit-a", "ali@a.com");
        var platform = await PlatformLoginAsync();

        var response = await _client.SendAsync(WithToken(HttpMethod.Get, url, platform.Token));

        // 403 (200 + boş liste DEĞİL): tenant_id claim'i olmayan token açıkça reddedilir.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _output.WriteLine($"✓ Kanıt: platform token'ı {url} adresinde 403 aldı — müşteri verisine ulaşamıyor.");
    }

    [Fact(DisplayName = "KANIT: platform token'ı satır içeriğini okuyamaz (403)")]
    public async Task PlatformToken_CannotReadRows()
    {
        var tenant = await RegisterTenantAsync("kanit-satir", "ali@satir.com");
        var datasetId = await SeedDataAsync(tenant.Token);
        var platform = await PlatformLoginAsync();

        var rows = await _client.SendAsync(WithToken(HttpMethod.Get,
            $"/api/datasets/{datasetId}/rows", platform.Token));
        var schema = await _client.SendAsync(WithToken(HttpMethod.Get,
            $"/api/datasets/{datasetId}/schema", platform.Token));
        var aggregate = await _client.SendAsync(WithToken(HttpMethod.Get,
            $"/api/datasets/{datasetId}/aggregate?op=count", platform.Token));

        Assert.Equal(HttpStatusCode.Forbidden, rows.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, schema.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, aggregate.StatusCode);
        _output.WriteLine("✓ Kanıt: satır, şema ve agregasyon uçlarının üçü de platform token'ına 403 döndü.");
    }

    [Fact(DisplayName = "KANIT: platform tenant listesi yalnız SAYI döner, veri içeriği taşımaz")]
    public async Task PlatformTenantList_ContainsOnlyMetadata()
    {
        var tenant = await RegisterTenantAsync("kanit-metadata", "ali@meta.com");
        await SeedDataAsync(tenant.Token);
        var platform = await PlatformLoginAsync();

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/tenants", platform.Token));
        var raw = await response.Content.ReadAsStringAsync();
        var tenants = (await response.Content.ReadFromJsonAsync<List<TenantSummary>>())!;

        var summary = Assert.Single(tenants);
        Assert.Equal(1, summary.UserCount);
        Assert.Equal(1, summary.DatasetCount);
        Assert.Equal(3, summary.RowCount);

        // Yanıtın HAM metninde ne satır içeriği ne veri seti adı ne kullanıcı e-postası olmalı.
        Assert.DoesNotContain("Ankara", raw);          // satır içeriği
        Assert.DoesNotContain("Satislar", raw);        // veri seti adı
        Assert.DoesNotContain("ali@meta.com", raw);    // kullanıcı e-postası
        Assert.DoesNotContain("sehir", raw);           // kolon adı
        _output.WriteLine("✓ Kanıt: firma özetinde 3 satır olduğu SAYI olarak görünüyor, ama " +
                          "satır içeriği (Ankara), veri seti adı, kolon adı ve kullanıcı e-postası yanıtta hiç yok.");
    }

    [Fact(DisplayName = "KANIT: tenant Admin'i platform uçlarına giremez (403)")]
    public async Task TenantAdminToken_CannotAccessPlatformEndpoints()
    {
        var tenant = await RegisterTenantAsync("kanit-b", "admin@b.com");
        Assert.Equal("Admin", tenant.Role); // firmayı açan kullanıcı Admin'dir

        var tenants = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/tenants", tenant.Token));
        var stats = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/stats", tenant.Token));
        var audit = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/audit-log", tenant.Token));

        Assert.Equal(HttpStatusCode.Forbidden, tenants.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stats.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
        _output.WriteLine("✓ Kanıt: firma Admin'i (en yetkili tenant rolü) platform uçlarının üçünde de 403 aldı — " +
                          "iki kimlik dünyası birbirine kapalı.");
    }

    // ---- Çapraz-tenant metadata ----

    [Fact(DisplayName = "Platform: tüm firmaları görür (çapraz-tenant metadata)")]
    public async Task PlatformAdmin_SeesAllTenants()
    {
        await RegisterTenantAsync("firma-a", "ali@a.com");
        await RegisterTenantAsync("firma-b", "ayse@b.com");
        var platform = await PlatformLoginAsync();

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/tenants", platform.Token));
        var tenants = (await response.Content.ReadFromJsonAsync<List<TenantSummary>>())!;

        Assert.Equal(2, tenants.Count);
        Assert.Contains(tenants, t => t.Slug == "firma-a");
        Assert.Contains(tenants, t => t.Slug == "firma-b");
        // Global query filter'ın tek meşru istisnası: yalnız metadata, yalnız platform katmanı.
        _output.WriteLine("✓ Platform yöneticisi iki firmayı da görüyor — izolasyon filtresi burada bilinçli olarak devre dışı.");
    }

    [Fact(DisplayName = "Platform: yeni firma varsayılan olarak AKTİF açılır")]
    public async Task NewTenant_IsActiveByDefault()
    {
        await RegisterTenantAsync("aktif-firma", "ali@aktif.com");
        var platform = await PlatformLoginAsync();

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/tenants", platform.Token));
        var tenants = (await response.Content.ReadFromJsonAsync<List<TenantSummary>>())!;

        Assert.True(Assert.Single(tenants).IsActive);
    }

    [Fact(DisplayName = "Platform: özet sayaçları doğru")]
    public async Task PlatformStats_AreCorrect()
    {
        var a = await RegisterTenantAsync("stat-a", "ali@stat.com");
        await RegisterTenantAsync("stat-b", "ayse@stat.com");
        await SeedDataAsync(a.Token);
        var platform = await PlatformLoginAsync();

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/stats", platform.Token));
        var stats = (await response.Content.ReadFromJsonAsync<PlatformStats>())!;

        Assert.Equal(2, stats.TenantCount);
        Assert.Equal(2, stats.ActiveTenantCount);
        Assert.Equal(0, stats.SuspendedTenantCount);
        Assert.Equal(2, stats.UserCount);
        Assert.Equal(1, stats.DatasetCount);
        Assert.Equal(3, stats.RowCount);
    }

    // ---- Askıya alma ----

    private async Task<HttpResponseMessage> SetStatusAsync(string platformToken, Guid tenantId,
        bool isActive) =>
        await _client.SendAsync(WithToken(HttpMethod.Put,
            $"/api/platform/tenants/{tenantId}/status", platformToken, new { isActive }));

    [Fact(DisplayName = "Askı: askıya alınan firmanın kullanıcısı giriş yapamaz, diğer firma etkilenmez")]
    public async Task SuspendedTenant_CannotLogin_OthersUnaffected()
    {
        var a = await RegisterTenantAsync("aski-a", "ali@aski.com");
        await RegisterTenantAsync("aski-b", "ayse@aski.com");
        var platform = await PlatformLoginAsync();

        (await SetStatusAsync(platform.Token, a.TenantId, false)).EnsureSuccessStatusCode();

        var suspendedLogin = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "ali@aski.com", password = "Sifre123!" });
        var otherLogin = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "ayse@aski.com", password = "Sifre123!" });

        Assert.Equal(HttpStatusCode.Unauthorized, suspendedLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherLogin.StatusCode);
        _output.WriteLine("✓ Askıya alınan firma giriş yapamıyor; diğer firma normal çalışmaya devam ediyor.");
    }

    [Fact(DisplayName = "Askı: ELDE DURAN access token da ANINDA reddedilir " +
                        "(15 dakikalık pencere kapandı)")]
    public async Task SuspendedTenant_ExistingAccessTokenIsRejectedImmediately()
    {
        // Kod incelemesinde bulunan ve en son kapatılan kalem.
        //
        // JWT kendi kendini doğrular: sunucu onu üretirken bildiklerini taşır ve süresi
        // dolana kadar hiçbir şey onu geri alamaz. Askı giriş, oturum yenileme, davet ve
        // izleyici taramasının dördünü de kapatıyordu ama ELDEKİ token çalışmaya devam
        // ediyordu. Yani "askıya aldım" demek aslında "en geç 15 dakika sonra kapanacak"
        // demekti; o pencerede kullanıcı bütün okuma uçlarını normal biçimde kullanıyordu.
        var a = await RegisterTenantAsync("aski-token", "ali@askitoken.com");
        var platform = await PlatformLoginAsync();

        // Askıdan ÖNCE alınmış token gerçekten çalışıyor.
        var once = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/datasets", a.Token));
        Assert.Equal(HttpStatusCode.OK, once.StatusCode);

        (await SetStatusAsync(platform.Token, a.TenantId, false)).EnsureSuccessStatusCode();

        // AYNI token, askıdan hemen sonra: artık geçmiyor. Beklemek gerekmiyor.
        var sonra = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/datasets", a.Token));

        Assert.Equal(HttpStatusCode.Unauthorized, sonra.StatusCode);
        _output.WriteLine("✓ Askı elde duran access token'ı anında geçersiz kılıyor.");
    }

    [Fact(DisplayName = "Askı kalkınca aynı token yeniden çalışıyor (sürdürme de anında)")]
    public async Task ResumedTenant_TokenWorksAgain()
    {
        // Snapshot düşürme sürdürmede de çağrılıyor: askı kalkan kullanıcı önbelleğin
        // süresi dolsun diye 30 saniye beklememeli.
        var a = await RegisterTenantAsync("aski-geri", "ali@askigeri.com");
        var platform = await PlatformLoginAsync();

        (await SetStatusAsync(platform.Token, a.TenantId, false)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.SendAsync(WithToken(HttpMethod.Get, "/api/datasets", a.Token)))
                .StatusCode);

        (await SetStatusAsync(platform.Token, a.TenantId, true)).EnsureSuccessStatusCode();

        var geri = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/datasets", a.Token));

        Assert.Equal(HttpStatusCode.OK, geri.StatusCode);
    }

    [Fact(DisplayName = "Askı: oturum YENİLEME de reddedilir (açık oturum sonsuza kadar yaşamaz)")]
    public async Task SuspendedTenant_CannotRefreshToken()
    {
        // Kullanıcı askıdan ÖNCE giriş yapmış → elinde geçerli bir refresh token var.
        var a = await RegisterTenantAsync("aski-refresh", "ali@refresh.com");
        var platform = await PlatformLoginAsync();

        (await SetStatusAsync(platform.Token, a.TenantId, false)).EnsureSuccessStatusCode();

        var refresh = await _client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = a.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        _output.WriteLine("✓ Kanıt: askıdan önce alınmış refresh token da çalışmıyor — " +
                          "aksi hâlde kullanıcı 7 gün boyunca sessizce token yenileyip erişmeye devam ederdi.");
    }

    [Fact(DisplayName = "Askı: geri alınabilir — yeniden etkinleştirilen firma tekrar giriş yapar")]
    public async Task ReactivatedTenant_CanLoginAgain()
    {
        var a = await RegisterTenantAsync("aski-geri", "ali@geri.com");
        var platform = await PlatformLoginAsync();

        await SetStatusAsync(platform.Token, a.TenantId, false);
        await SetStatusAsync(platform.Token, a.TenantId, true);

        var login = await _client.PostAsJsonAsync("/api/auth/login",
            new { email = "ali@geri.com", password = "Sifre123!" });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact(DisplayName = "Askı: veri SİLİNMEZ, sayılar korunur")]
    public async Task SuspendedTenant_DataIsPreserved()
    {
        var a = await RegisterTenantAsync("aski-veri", "ali@veri.com");
        await SeedDataAsync(a.Token);
        var platform = await PlatformLoginAsync();

        await SetStatusAsync(platform.Token, a.TenantId, false);

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/tenants", platform.Token));
        var summary = Assert.Single((await response.Content.ReadFromJsonAsync<List<TenantSummary>>())!);

        Assert.False(summary.IsActive);
        Assert.NotNull(summary.SuspendedAt);
        Assert.Equal(3, summary.RowCount); // askı erişimi kapatır, veriyi yok etmez
    }

    [Fact(DisplayName = "Askı: olmayan firma 404 döner")]
    public async Task SuspendUnknownTenant_NotFound()
    {
        var platform = await PlatformLoginAsync();

        var response = await SetStatusAsync(platform.Token, Guid.NewGuid(), false);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Denetim kaydı ----

    [Fact(DisplayName = "Denetim: askıya alma ve etkinleştirme kayda geçer")]
    public async Task PlatformActions_AreAudited()
    {
        var a = await RegisterTenantAsync("denetim", "ali@denetim.com");
        var platform = await PlatformLoginAsync();

        await SetStatusAsync(platform.Token, a.TenantId, false);
        await SetStatusAsync(platform.Token, a.TenantId, true);

        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, "/api/platform/audit-log", platform.Token));
        var logs = (await response.Content.ReadFromJsonAsync<List<AuditEntry>>())!;

        Assert.Contains(logs, l => l.Action == "TenantSuspended" && l.TargetTenantId == a.TenantId);
        Assert.Contains(logs, l => l.Action == "TenantActivated" && l.TargetTenantId == a.TenantId);
        Assert.Contains(logs, l => l.Action == "PlatformLogin");
        Assert.All(logs, l => Assert.Equal(ApiFactory.PlatformAdminEmail, l.PlatformAdminEmail));
        _output.WriteLine("✓ Denetim izi: kim (e-posta), ne (askı/etkinleştirme/giriş), hangi firmaya — hepsi kayıtlı.");
    }

    // ---- Şifre değiştirme ----

    [Fact(DisplayName = "Platform: şifre değiştirilir, eski şifre artık çalışmaz")]
    public async Task PlatformAdmin_CanChangePassword()
    {
        var platform = await PlatformLoginAsync();
        const string newPassword = "YeniPlatformSifre456!";

        var change = await _client.SendAsync(WithToken(HttpMethod.Post,
            "/api/platform/auth/change-password", platform.Token,
            new { currentPassword = ApiFactory.PlatformAdminPassword, newPassword }));
        change.EnsureSuccessStatusCode();

        var withOld = await _client.PostAsJsonAsync("/api/platform/auth/login",
            new { email = ApiFactory.PlatformAdminEmail, password = ApiFactory.PlatformAdminPassword });
        var withNew = await _client.PostAsJsonAsync("/api/platform/auth/login",
            new { email = ApiFactory.PlatformAdminEmail, password = newPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
        _output.WriteLine("✓ Ayarlardaki tohum şifre değiştirilebiliyor — işletmeci ilk girişten sonra " +
                          "env'deki açık şifreyi silebilir, kimlik DB'de hash olarak yaşamaya devam eder.");
    }

    [Fact(DisplayName = "Platform: yanlış mevcut şifreyle değiştirme reddedilir")]
    public async Task PlatformAdmin_ChangePassword_RequiresCurrentPassword()
    {
        var platform = await PlatformLoginAsync();

        var change = await _client.SendAsync(WithToken(HttpMethod.Post,
            "/api/platform/auth/change-password", platform.Token,
            new { currentPassword = "AlakasizSifre1!", newPassword = "YeniSifre456!" }));

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
    }

    [Fact(DisplayName = "Platform: şifre değiştirme token'sız yapılamaz (401)")]
    public async Task PlatformAdmin_ChangePassword_RequiresToken()
    {
        var response = await _client.PostAsJsonAsync("/api/platform/auth/change-password",
            new { currentPassword = ApiFactory.PlatformAdminPassword, newPassword = "YeniSifre456!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
