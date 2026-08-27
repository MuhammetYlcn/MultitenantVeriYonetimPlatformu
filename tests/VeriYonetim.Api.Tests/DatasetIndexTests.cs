using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Tests;

// Kolon bazlı arama indeksi.
//
// Buradaki testlerin en önemlisi "indeks kuruldu mu" değil, PostgreSQL'in onu
// KULLANIP kullanmadığı: indekslenen ifade sorgunun ürettiği ifadeyle birebir aynı
// değilse indeks sessizce boşta durur, kullanıcı ise hızlandırdığını sanır. Bu, ölçüm
// yapılmadan fark edilemeyecek bir kusur olurdu.
public class DatasetIndexTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public DatasetIndexTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private record TokenResponse(Guid UserId, Guid TenantId, string Email, string Role,
        string Token, string RefreshToken);

    private record DatasetDto(Guid Id, string Name, string? Description, int RowCount,
        DateTime CreatedAt, DateTime? UpdatedAt);

    private record SchemaColumn(string Name, string Type, int Ordinal, bool Indexed, bool CanIndex);
    private record SchemaResponse(Guid DatasetId, List<SchemaColumn> Columns);

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string token,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<HttpResponseMessage> UploadAsync(string token, string url, string csv)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "test.csv");

        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private async Task<(string Token, Guid Id)> SeededAsync(string slug, string email)
    {
        var register = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = slug, email, password = "Sifre123!" });
        register.EnsureSuccessStatusCode();
        var t = (await register.Content.ReadFromJsonAsync<TokenResponse>())!;

        var create = await _client.SendAsync(
            WithToken(HttpMethod.Post, "/api/datasets", t.Token, new { name = "S" }));
        create.EnsureSuccessStatusCode();
        var id = (await create.Content.ReadFromJsonAsync<DatasetDto>())!.Id;

        await UploadAsync(t.Token, $"/api/datasets/{id}/schema",
            "ad,tutar,tarih\nAli,10,2026-01-15");
        await UploadAsync(t.Token, $"/api/datasets/{id}/rows",
            "ad,tutar,tarih\nAli,10,2026-01-15\nAyse,20,2026-02-15\nVeli,30,2026-03-15");

        return (t.Token, id);
    }

    private async Task<string> RegisterOnlyAsync(string slug, string email)
    {
        var register = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = slug, email, password = "Sifre123!" });
        register.EnsureSuccessStatusCode();
        return (await register.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
    }

    private async Task<HttpResponseMessage> IndexAsync(string token, Guid id, string column) =>
        await _client.SendAsync(WithToken(
            HttpMethod.Post, $"/api/datasets/{id}/columns/{column}/index", token));

    private async Task<SchemaResponse> SchemaAsync(string token, Guid id)
    {
        var response = await _client.SendAsync(
            WithToken(HttpMethod.Get, $"/api/datasets/{id}/schema", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SchemaResponse>())!;
    }

    [Fact]
    public async Task IndexingTextColumn_MakesPostgresUseTheIndex()
    {
        var (token, id) = await SeededAsync("ix-text", "a@ixtext.com");

        var response = await IndexAsync(token, id, "ad");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Asıl doğrulama: sorgunun planı. İndekslenen ifade `lower(...)` altında;
        // sorgu da metin eşitliğini `lower()` ile kuruyor. İkisi ayrışsaydı plan
        // yine tabloyu tarardı ve indeks boşa yatırım olurdu.
        var plan = await PlanAsync(id, """lower(("Data"->>'ad')) = lower('Ali')""");
        Assert.Contains("Index", plan);
    }

    [Fact]
    public async Task IndexingNumberColumn_MakesPostgresUseTheIndex()
    {
        var (token, id) = await SeededAsync("ix-num", "a@ixnum.com");

        Assert.Equal(HttpStatusCode.OK, (await IndexAsync(token, id, "tutar")).StatusCode);

        var plan = await PlanAsync(id, """(("Data"->>'tutar'))::numeric >= 20""");
        Assert.Contains("Index", plan);
    }

    [Fact]
    public async Task IndexingDateColumn_IsRefusedWithAReason()
    {
        // 24.08 ölçümünde bu aday `must be marked IMMUTABLE` hatasıyla düşmüştü.
        // Uç bunu bir çökme olarak değil, açıklamalı bir cevap olarak veriyor.
        var (token, id) = await SeededAsync("ix-date", "a@ixdate.com");

        var response = await IndexAsync(token, id, "tarih");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Tarih", body);
    }

    [Fact]
    public async Task SchemaReportsWhichColumnsAreIndexedAndWhichCanBe()
    {
        var (token, id) = await SeededAsync("ix-schema", "a@ixschema.com");

        await IndexAsync(token, id, "tutar");

        var schema = await SchemaAsync(token, id);

        Assert.True(schema.Columns.Single(c => c.Name == "tutar").Indexed);
        Assert.False(schema.Columns.Single(c => c.Name == "ad").Indexed);

        // Tarih kolonu için düğme hiç gösterilmemeli.
        Assert.False(schema.Columns.Single(c => c.Name == "tarih").CanIndex);
        Assert.True(schema.Columns.Single(c => c.Name == "ad").CanIndex);
    }

    [Fact]
    public async Task ConflictingColumnTypeAcrossDatasets_IsRefusedWithAReason()
    {
        // Gerçek veride ilk denemede çıkan kusur: `tutar` bir sette sayı, başka bir
        // sette metin ("1.500,50"). Satırlar tek bir tabloda durduğundan indeks de
        // ortaktır ve sayı indeksi metin satırlarında çöker. Ölçüm ortamında her set
        // aynı şemadan türetildiği için bu hiç görünmemişti.
        var (tokenA, idA) = await SeededAsync("ix-conf-a", "a@ixconfa.com");

        // İkinci firma aynı adı METİN kolon olarak kullanıyor.
        var register = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = "ix-conf-b", email = "b@ixconfb.com", password = "Sifre123!" });
        register.EnsureSuccessStatusCode();
        var b = (await register.Content.ReadFromJsonAsync<TokenResponse>())!;

        var create = await _client.SendAsync(
            WithToken(HttpMethod.Post, "/api/datasets", b.Token, new { name = "TR" }));
        create.EnsureSuccessStatusCode();
        var idB = (await create.Content.ReadFromJsonAsync<DatasetDto>())!.Id;

        // Türkçe ondalık ayracı yüzünden kolon metin olarak algılanıyor.
        await UploadAsync(b.Token, $"/api/datasets/{idB}/schema", "ad,tutar\nAli,1.500 TL");
        await UploadAsync(b.Token, $"/api/datasets/{idB}/rows", "ad,tutar\nAli,1.500 TL");

        var response = await IndexAsync(tokenA, idA, "tutar");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("farklı bir tiple", body);

        // MESAJ DİĞER FİRMANIN TİPİNİ SÖYLEMEMELİ.
        //
        // Eskiden söylüyordu ("… farklı tipte (text) kullanılıyor") ve bu, platformdaki
        // bütün firmaların şema metadata'sına karşı bir sorgulama aracıydı: A kendi
        // setine tek kolonluk bir şema yükleyip indeks isteyerek "bu adda kolon var mı,
        // tipi ne" sorusunu cevaplatabiliyordu. Kolon adını değiştirerek (vergi_no,
        // tckn, maas…) bütün ad uzayı taranabilirdi. Kullanıcının yapacağı iş kolonu
        // yeniden adlandırmak olduğu için tipi bilmesi zaten gerekmiyor.
        Assert.DoesNotContain("text", body);
    }

    [Fact(DisplayName = "TERS YÖN: indeks dururken çakışan şema REDDEDİLİR " +
                        "(yoksa satır yazma boş bir 500'e düşerdi)")]
    public async Task ConflictingSchema_IsRefusedWhileIndexExists()
    {
        // Kod incelemesinde bulunan kusur: çakışma denetimi YALNIZCA indeks kurulurken
        // yapılıyordu. Ters yön — indeks zaten dururken aynı adı farklı tiple taşıyan
        // yeni bir şema kaydedilmesi — hiç denetlenmiyordu.
        //
        // Sonucu şuydu: A firması `tutar`ı sayı olarak indeksliyor (indeks TABLO GENELİ).
        // B firması `tutar` sütununda "1.500,50 TL" yazan bir CSV yüklüyor, şema 200
        // dönüyor. Satırları yazarken COPY, PostgreSQL indeks ifadesini değerlendirdiği
        // için düşüyor ve B BOŞ BİR 500 alıyor — o dosyayı bir daha asla içeri alamıyor,
        // sebebini göremiyor, A'nın indeksine de erişemiyor.
        var (tokenA, idA) = await SeededAsync("ix-ters-a", "a@ixtersa.com");

        // A firması `tutar`ı SAYI olarak indeksliyor.
        (await IndexAsync(tokenA, idA, "tutar")).EnsureSuccessStatusCode();

        // B firması aynı adı METİN taşıyan bir dosyayla geliyor.
        var register = await _client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = "ix-ters-b", email = "b@ixtersb.com", password = "Sifre123!" });
        register.EnsureSuccessStatusCode();
        var b = (await register.Content.ReadFromJsonAsync<TokenResponse>())!;

        var create = await _client.SendAsync(
            WithToken(HttpMethod.Post, "/api/datasets", b.Token, new { name = "TR" }));
        create.EnsureSuccessStatusCode();
        var idB = (await create.Content.ReadFromJsonAsync<DatasetDto>())!.Id;

        var schema = await UploadAsync(b.Token, $"/api/datasets/{idB}/schema",
            "ad,tutar\nAli,1.500 TL");

        // Şema adımında, SEBEBİYLE reddediliyor — satır yazmada boş bir 500 olarak değil.
        Assert.Equal(HttpStatusCode.Conflict, schema.StatusCode);
        Assert.Contains("tutar", await schema.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FailedIndexCreation_LeavesNoHalfBuiltIndexBehind()
    {
        // PostgreSQL, CONCURRENTLY kurulumu başarısız olduğunda indeksi SİLMEZ, geçersiz
        // olarak bırakır. Temizlenmezse bir sonraki deneme `IF NOT EXISTS` yüzünden onu
        // "zaten var" sayar, kayıt eklenir ve kullanıcı hızlandırdığını sanır — oysa
        // sorgular o indeksi kullanamaz. Sessiz yanlış cevabın ta kendisi.
        var (token, id) = await SeededAsync("ix-halfbuilt", "a@ixhalf.com");

        // Şemaya göre sayı olan kolona, şemayı atlayarak sayı olmayan bir değer koy.
        // Bu, doğrulamanın kaçırdığı bir satırın (ya da elle düzenlenmiş verinin)
        // karşılığı: kurulum veritabanı tarafında düşer.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DatasetRows" ("Id", "Data", "DatasetId")
                VALUES ({0}, CAST({1} AS jsonb), {2})
                """, Guid.NewGuid(), """{"ad":"X","tutar":"sayi degil"}""", id);
        }

        var response = await IndexAsync(token, id, "tutar");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("çevrilemeyen", await response.Content.ReadAsStringAsync());

        // Asıl doğrulama: arkada yarım indeks kalmadı.
        Assert.Empty(await InvalidIndexesAsync());

        // Ve kolon indeksli GÖRÜNMÜYOR — kayıt da eklenmemiş olmalı.
        Assert.False((await SchemaAsync(token, id)).Columns.Single(c => c.Name == "tutar").Indexed);
    }

    [Fact]
    public async Task IndexingTwice_IsRefused()
    {
        var (token, id) = await SeededAsync("ix-twice", "a@ixtwice.com");

        Assert.Equal(HttpStatusCode.OK, (await IndexAsync(token, id, "ad")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await IndexAsync(token, id, "ad")).StatusCode);
    }

    [Fact]
    public async Task UnknownColumn_IsRefused()
    {
        var (token, id) = await SeededAsync("ix-unknown", "a@ixunknown.com");

        var response = await IndexAsync(token, id, "olmayan");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DroppingIndex_RemovesItFromSchema_AndFromPostgres()
    {
        var (token, id) = await SeededAsync("ix-drop", "a@ixdrop.com");

        await IndexAsync(token, id, "ad");
        var indexName = await SingleIndexNameAsync();

        var response = await _client.SendAsync(WithToken(
            HttpMethod.Delete, $"/api/datasets/{id}/columns/ad/index", token));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False((await SchemaAsync(token, id)).Columns.Single(c => c.Name == "ad").Indexed);
        Assert.False(await IndexExistsAsync(indexName));
    }

    [Fact]
    public async Task SharedPhysicalIndex_SurvivesUntilTheLastDatasetDropsIt()
    {
        // Satırlar tek bir tabloda durduğu için fiziksel indeks kolon ADI bazındadır:
        // aynı adı taşıyan iki set aynı indeksi paylaşır. Biri vazgeçince indeks
        // düşürülemez — diğeri hâlâ ona dayanıyor.
        var (tokenA, idA) = await SeededAsync("ix-share-a", "a@ixsharea.com");
        var (tokenB, idB) = await SeededAsync("ix-share-b", "b@ixshareb.com");

        await IndexAsync(tokenA, idA, "ad");
        await IndexAsync(tokenB, idB, "ad");

        var indexName = await SingleIndexNameAsync();

        await _client.SendAsync(WithToken(
            HttpMethod.Delete, $"/api/datasets/{idA}/columns/ad/index", tokenA));

        Assert.True(await IndexExistsAsync(indexName));   // B hâlâ kullanıyor

        await _client.SendAsync(WithToken(
            HttpMethod.Delete, $"/api/datasets/{idB}/columns/ad/index", tokenB));

        Assert.False(await IndexExistsAsync(indexName));  // son kayıt da gitti
    }

    [Fact]
    public async Task CrossTenant_CannotIndexAnotherTenantsColumn()
    {
        var (_, idA) = await SeededAsync("ix-x-a", "a@ixxa.com");
        var bToken = await RegisterOnlyAsync("ix-x-b", "b@ixxb.com");

        var response = await IndexAsync(bToken, idA, "ad");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- veritabanına doğrudan bakan yardımcılar --------------------------------------

    private async Task<string> PlanAsync(Guid datasetId, string condition)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            // Üç satırlık bir tabloda planlayıcı indeksi seçmez — tarama zaten daha
            // ucuzdur. Ölçülen şey indeksin TERCİH edilmesi değil, KULLANILABİLİR olması:
            // ifade eşleşmiyorsa tarama kapatılsa bile indeks kullanılamaz.
            //
            // Ayar bir işlemin içinde yapılıyor: `SET LOCAL` işlem dışında sessizce
            // etkisiz kalır ve test bazen geçip bazen düşerdi.
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var setting = connection.CreateCommand())
            {
                setting.Transaction = transaction;
                setting.CommandText = "SET LOCAL enable_seqscan = off";
                await setting.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"""EXPLAIN SELECT * FROM "DatasetRows" WHERE "DatasetId" = '{datasetId}' AND {condition}""";

            var plan = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
            }

            await transaction.RollbackAsync();

            return string.Join("\n", plan);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task<string> SingleIndexNameAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.DatasetIndexes
            .IgnoreQueryFilters()
            .Select(i => i.IndexName)
            .Distinct()
            .SingleAsync();
    }

    // Yarım kalmış (indisvalid = false) indeksler. Boş olmalı: geçersiz bir indeks
    // sorgularda kullanılmaz ama adı doludur ve "zaten var" görünür.
    private async Task<List<string>> InvalidIndexesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.relname FROM pg_index i
                JOIN pg_class c ON c.oid = i.indexrelid
                WHERE c.relname LIKE 'ix\_rows\_%' AND NOT i.indisvalid
                """;

            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));

            return names;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task<bool> IndexExistsAsync(string indexName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT COUNT(*) FROM pg_indexes WHERE indexname = '{indexName}'";

            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
