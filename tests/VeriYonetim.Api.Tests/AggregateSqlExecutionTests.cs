using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Üretilen SQL'i GERÇEK PostgreSQL üzerinde çalıştırır.
//
// Neden ayrı bir sınıf? Builder birim testleri SQL METNİNİ doğrular — "AVG( geçiyor mu",
// "OVER () var mı" gibi. Metin doğru görünüp sorgu yine de patlayabilir: pencere
// fonksiyonunun yeri, HAVING'in alias kabul etmemesi, GROUP BY ile SELECT uyumu gibi
// kurallar ancak veritabanı çalıştırınca ortaya çıkar. Seviye 2-3'te eklenen yetenekler
// bu yüzden burada uçtan uca koşturuluyor.
public class AggregateSqlExecutionTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private Guid _datasetId;

    public AggregateSqlExecutionTests(ApiFactory factory) => _factory = factory;

    private static readonly Dictionary<string, string> Schema = new()
    {
        ["sehir"] = "text",
        ["kategori"] = "text",
        ["tutar"] = "number",
        ["tarih"] = "date"
    };

    // Şehir/kategori/tutar/tarih içeren 6 satır. Beklenen değerler:
    //   Ankara: 100 + 150 + 50 = 300 (3 satır)   Bursa: 200 (1 satır)
    //   Izmir : 40 + 10 = 50        (2 satır)
    private static readonly (string Sehir, string Kategori, decimal Tutar, string Tarih)[] Rows =
    {
        ("Ankara", "Elektronik", 100m, "2026-03-01"),
        ("Ankara", "Gida",       150m, "2026-03-05"),
        ("Ankara", "Elektronik",  50m, "2026-03-09"),
        ("Bursa",  "Elektronik", 200m, "2026-03-02"),
        ("Izmir",  "Gida",        40m, "2026-03-03"),
        ("Izmir",  "Gida",        10m, "2026-03-04"),
    };

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Kayıtlar ham SQL ile atılıyor: EF global query filter'ı ve TenantContext'i
        // devreye sokmadan, yalnız SQL'i sınamak için gereken en az kurulum.
        var tenantId = Guid.NewGuid();
        _datasetId = Guid.NewGuid();

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Tenants" ("Id", "Name", "Slug", "SchemaName", "CreatedAt", "IsActive")
            VALUES ({0}, 'SqlTest', 'sqltest', 'tenant_sqltest', now(), true)
            """, tenantId);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Datasets" ("Id", "Name", "RowCount", "CreatedAt", "TenantId")
            VALUES ({0}, 'Satislar', {1}, now(), {2})
            """, _datasetId, Rows.Length, tenantId);

        foreach (var r in Rows)
        {
            var json = $$"""
                {"sehir":"{{r.Sehir}}","kategori":"{{r.Kategori}}","tutar":{{r.Tutar.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"tarih":"{{r.Tarih}}"}
                """;

            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DatasetRows" ("Id", "Data", "DatasetId")
                VALUES ({0}, CAST({1} AS jsonb), {2})
                """, Guid.NewGuid(), json, _datasetId);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Sorguyu çalıştırır ve satırları (anahtarlar, değerler, sayı, pay) olarak döndürür.
    private async Task<List<(string?[] Keys, decimal?[] Values, int Count, decimal? Share)>>
        RunAsync(AggregateQuery query)
    {
        var built = DatasetAggregateQueryBuilder.Build(query, Schema);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = db.Database.GetDbConnection();

        var results = new List<(string?[], decimal?[], int, decimal?)>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = built.Sql;
        cmd.Parameters.Add(new NpgsqlParameter("datasetId", _datasetId));
        foreach (var p in built.Parameters) cmd.Parameters.Add(p);

        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var keys = new string?[built.KeyCount];
                for (var i = 0; i < built.KeyCount; i++)
                    keys[i] = reader.IsDBNull(i) ? null : reader.GetString(i);

                var values = new decimal?[built.MetricCount];
                for (var i = 0; i < built.MetricCount; i++)
                {
                    var o = built.KeyCount + i;
                    values[i] = reader.IsDBNull(o) ? null : reader.GetDecimal(o);
                }

                var countOrdinal = built.KeyCount + built.MetricCount;
                decimal? share = built.HasShare && !reader.IsDBNull(countOrdinal + 1)
                    ? reader.GetDecimal(countOrdinal + 1)
                    : null;

                results.Add((keys, values, reader.GetInt32(countOrdinal), share));
            }
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }

        return results;
    }

    private static AggregateQuery Q(
        string[]? groupBy = null, MetricSpec[]? metrics = null, FilterNode[]? filters = null,
        HavingSpec? having = null, bool share = false, string? sort = null, string? dir = null,
        int? limit = null, int sortMetric = 0) =>
        new(groupBy ?? Array.Empty<string>(),
            metrics ?? new[] { new MetricSpec("count") },
            null, sort, dir, limit,
            filters ?? Array.Empty<FilterNode>(),
            having, sortMetric, share);

    [Fact]
    public async Task MultipleMetrics_ReturnCorrectValues()
    {
        var rows = await RunAsync(Q(
            groupBy: new[] { "sehir" },
            metrics: new[]
            {
                new MetricSpec("sum", "tutar"),
                new MetricSpec("avg", "tutar"),
                new MetricSpec("count")
            }));

        var ankara = rows.Single(r => r.Keys[0] == "Ankara");
        Assert.Equal(300m, ankara.Values[0]);
        Assert.Equal(100m, ankara.Values[1]);   // 300 / 3
        Assert.Equal(3m, ankara.Values[2]);
        Assert.Equal(3, ankara.Count);
    }

    [Fact]
    public async Task MultipleGroupBy_ProducesCombinationRows()
    {
        var rows = await RunAsync(Q(
            groupBy: new[] { "sehir", "kategori" },
            metrics: new[] { new MetricSpec("sum", "tutar") }));

        // Ankara/Elektronik (100+50), Ankara/Gida, Bursa/Elektronik, Izmir/Gida
        Assert.Equal(4, rows.Count);
        var ankaraElektronik = rows.Single(r => r.Keys[0] == "Ankara" && r.Keys[1] == "Elektronik");
        Assert.Equal(150m, ankaraElektronik.Values[0]);
    }

    [Fact]
    public async Task CountDistinct_CountsUniqueValues()
    {
        var rows = await RunAsync(Q(metrics: new[] { new MetricSpec("countDistinct", "sehir") }));

        // Ankara, Bursa, Izmir → 3 (satır sayısı 6 olmasına rağmen)
        Assert.Equal(3m, Assert.Single(rows).Values[0]);
    }

    [Fact]
    public async Task Having_FiltersGroupsAfterAggregation()
    {
        // "Toplamı 100'ün üstünde olan şehirler" → Ankara (300), Bursa (200). Izmir (50) elenir.
        var rows = await RunAsync(Q(
            groupBy: new[] { "sehir" },
            metrics: new[] { new MetricSpec("sum", "tutar") },
            having: new HavingSpec(0, "gt", 100m)));

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Keys[0] == "Izmir");
    }

    [Fact]
    public async Task Share_PercentagesSumToOneHundred()
    {
        var rows = await RunAsync(Q(
            groupBy: new[] { "sehir" },
            metrics: new[] { new MetricSpec("sum", "tutar") },
            share: true));

        // Toplam 550: Ankara 300 (%54.54), Bursa 200 (%36.36), Izmir 50 (%9.09)
        var ankara = rows.Single(r => r.Keys[0] == "Ankara");
        Assert.NotNull(ankara.Share);
        Assert.Equal(54.55m, Math.Round(ankara.Share!.Value, 2));

        var total = rows.Sum(r => r.Share ?? 0m);
        Assert.Equal(100m, Math.Round(total, 2));
    }

    [Fact]
    public async Task OrFilterTree_MatchesEitherBranch()
    {
        // "Izmir'deki VEYA 200 TL üstü satışlar" → Izmir'in 2 satırı + Bursa'nın 200'ü
        var rows = await RunAsync(Q(
            metrics: new[] { new MetricSpec("count") },
            filters: new FilterNode[]
            {
                new FilterGroup("or", new FilterNode[]
                {
                    new RowFilter("sehir", "eq", "Izmir"),
                    new RowFilter("tutar", "gte", "200")
                })
            }));

        Assert.Equal(3m, Assert.Single(rows).Values[0]);
    }

    [Fact]
    public async Task InFilter_MatchesAnyOfTheValues()
    {
        // "Ankara ve Izmir'deki satışlar" — iki eq filtresi VE'lenseydi 0 satır dönerdi.
        var rows = await RunAsync(Q(
            metrics: new[] { new MetricSpec("count") },
            filters: new FilterNode[] { new RowFilter("sehir", "in", "", new[] { "Ankara", "Izmir" }) }));

        Assert.Equal(5m, Assert.Single(rows).Values[0]);   // 3 Ankara + 2 Izmir
    }

    [Fact]
    public async Task SortByChosenMetric_OrdersCorrectly()
    {
        var rows = await RunAsync(Q(
            groupBy: new[] { "sehir" },
            metrics: new[] { new MetricSpec("count"), new MetricSpec("sum", "tutar") },
            sort: "value", dir: "desc", sortMetric: 1, limit: 2));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Ankara", rows[0].Keys[0]);   // 300
        Assert.Equal("Bursa", rows[1].Keys[0]);    // 200
    }

    [Fact]
    public async Task HavingAndShare_WorkTogether()
    {
        // Pay, HAVING'den SONRA kalan grupların toplamına göre hesaplanır: Izmir elendiği
        // için payda 550 değil 500'dür (Ankara 300 + Bursa 200).
        var rows = await RunAsync(Q(
            groupBy: new[] { "sehir" },
            metrics: new[] { new MetricSpec("sum", "tutar") },
            having: new HavingSpec(0, "gt", 100m),
            share: true));

        var ankara = rows.Single(r => r.Keys[0] == "Ankara");
        Assert.Equal(60m, Math.Round(ankara.Share!.Value, 2));   // 300 / 500
    }
}
