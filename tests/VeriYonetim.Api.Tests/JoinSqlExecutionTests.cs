using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// İki veri setini birleştiren sorguları GERÇEK PostgreSQL üzerinde çalıştırır.
//
// Buradaki asıl risk metin doğrulamayla yakalanamaz: JOIN'in ON koşulu, takma adların
// tutarlılığı ve veri seti kimliklerinin doğru parametreye bağlanması ancak sorgu koşunca
// belli olur. Yanlış kurulmuş bir JOIN hata vermez — sessizce ya boş sonuç ya da
// katlanmış (çarpılmış) sayılar döndürür.
public class JoinSqlExecutionTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private Guid _satislarId;
    private Guid _musterilerId;
    private TenantCatalog _catalog = null!;

    public JoinSqlExecutionTests(ApiFactory factory) => _factory = factory;

    // Musteriler: M1/Ankara, M2/Izmir, M3/Ankara
    // Satislar  : M1→100, M1→150, M2→200, M3→50
    // Beklenen  : şehir bazında Ankara 300 (250 + 50), Izmir 200
    private static readonly (string No, string Ad, string Sehir)[] Musteriler =
    {
        ("M1", "Ali", "Ankara"),
        ("M2", "Ayse", "Izmir"),
        ("M3", "Veli", "Ankara"),
    };

    private static readonly (string MusteriNo, decimal Tutar, string Tarih)[] Satislar =
    {
        ("M1", 100m, "2026-03-01"),
        ("M1", 150m, "2026-03-05"),
        ("M2", 200m, "2026-03-02"),
        ("M3",  50m, "2026-03-08"),
    };

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenantId = Guid.NewGuid();
        _satislarId = Guid.NewGuid();
        _musterilerId = Guid.NewGuid();

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Tenants" ("Id", "Name", "Slug", "SchemaName", "CreatedAt", "IsActive")
            VALUES ({0}, 'JoinTest', 'jointest', 'tenant_jointest', now(), true)
            """, tenantId);

        await InsertDatasetAsync(db, _satislarId, tenantId, "Satislar", Satislar.Length);
        await InsertDatasetAsync(db, _musterilerId, tenantId, "Musteriler", Musteriler.Length);

        foreach (var m in Musteriler)
            await InsertRowAsync(db, _musterilerId,
                $$"""{"no":"{{m.No}}","ad":"{{m.Ad}}","sehir":"{{m.Sehir}}"}""");

        foreach (var s in Satislar)
            await InsertRowAsync(db, _satislarId,
                $$"""{"musteri_no":"{{s.MusteriNo}}","tutar":{{s.Tutar.ToString(CultureInfo.InvariantCulture)}},"tarih":"{{s.Tarih}}"}""");

        _catalog = new TenantCatalog(
            new[]
            {
                new DatasetInfo(_satislarId, "Satislar", null, new Dictionary<string, string>
                {
                    ["musteri_no"] = "text", ["tutar"] = "number", ["tarih"] = "date"
                }),
                new DatasetInfo(_musterilerId, "Musteriler", null, new Dictionary<string, string>
                {
                    ["no"] = "text", ["ad"] = "text", ["sehir"] = "text"
                })
            },
            new[] { new RelationInfo(_satislarId, "musteri_no", _musterilerId, "no") });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Task InsertDatasetAsync(AppDbContext db, Guid id, Guid tenantId, string name, int rows) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Datasets" ("Id", "Name", "RowCount", "CreatedAt", "TenantId")
            VALUES ({0}, {1}, {2}, now(), {3})
            """, id, name, rows, tenantId);

    private static Task InsertRowAsync(AppDbContext db, Guid datasetId, string json) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "DatasetRows" ("Id", "Data", "DatasetId")
            VALUES ({0}, CAST({1} AS jsonb), {2})
            """, Guid.NewGuid(), json, datasetId);

    private async Task<List<(string?[] Keys, decimal?[] Values, int Count)>> RunAsync(
        AggregateQuery query, QueryScope scope)
    {
        var built = DatasetAggregateQueryBuilder.Build(query, scope);

        using var s = _factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = db.Database.GetDbConnection();

        var results = new List<(string?[], decimal?[], int)>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = built.Sql;
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

                results.Add((keys, values, reader.GetInt32(built.KeyCount + built.MetricCount)));
            }
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }

        return results;
    }

    private static AggregateQuery Q(string[]? groupBy = null, MetricSpec[]? metrics = null,
        FilterNode[]? filters = null) =>
        new(groupBy ?? Array.Empty<string>(),
            metrics ?? new[] { new MetricSpec("count") },
            null, null, null, null, filters ?? Array.Empty<FilterNode>());

    [Fact]
    public async Task Join_GroupsByColumnFromTheOtherDataset()
    {
        // "Satışları müşterinin şehrine göre grupla" — şehir Satislar'da YOK, Musteriler'de.
        var scope = _catalog.BuildScope(new[] { "Satislar", "Musteriler" });

        var rows = await RunAsync(
            Q(groupBy: new[] { "Musteriler.sehir" },
              metrics: new[] { new MetricSpec("sum", "Satislar.tutar") }), scope);

        Assert.Equal(2, rows.Count);
        Assert.Equal(300m, rows.Single(r => r.Keys[0] == "Ankara").Values[0]);   // 100 + 150 + 50
        Assert.Equal(200m, rows.Single(r => r.Keys[0] == "Izmir").Values[0]);
    }

    [Fact]
    public async Task Join_DoesNotMultiplyRows()
    {
        // Yanlış kurulmuş bir JOIN sayıları katlar ve bunu hata vermeden yapar.
        // Satış sayısı 4; birleştirme sonrası da 4 olmalı.
        var scope = _catalog.BuildScope(new[] { "Satislar", "Musteriler" });

        var rows = await RunAsync(Q(metrics: new[] { new MetricSpec("count") }), scope);

        Assert.Equal(4m, Assert.Single(rows).Values[0]);
    }

    [Fact]
    public async Task Join_FilterOnJoinedDataset()
    {
        // "Ankaralı müşterilerin toplam satışı" — filtre öteki setin kolonunda.
        var scope = _catalog.BuildScope(new[] { "Satislar", "Musteriler" });

        var rows = await RunAsync(
            Q(metrics: new[] { new MetricSpec("sum", "Satislar.tutar") },
              filters: new FilterNode[] { new RowFilter("Musteriler.sehir", "eq", "Ankara") }), scope);

        Assert.Equal(300m, Assert.Single(rows).Values[0]);
    }

    [Fact]
    public void Join_EmitsAliasesAndBindsBothDatasetIds()
    {
        var scope = _catalog.BuildScope(new[] { "Satislar", "Musteriler" });
        var built = DatasetAggregateQueryBuilder.Build(
            Q(groupBy: new[] { "Musteriler.sehir" }, metrics: new[] { new MetricSpec("count") }), scope);

        Assert.Contains("JOIN \"DatasetRows\" d1", built.Sql);
        Assert.Contains("d1.\"DatasetId\" = @ds_d1", built.Sql);
        Assert.Contains("d0.\"DatasetId\" = @ds_d0", built.Sql);

        // Kimlikler SQL metnine gömülmemeli.
        Assert.DoesNotContain(_satislarId.ToString(), built.Sql);
        Assert.DoesNotContain(_musterilerId.ToString(), built.Sql);
    }

    [Fact]
    public async Task SingleDataset_ProducesNoAliases()
    {
        // Tek veri setli sorgunun SQL'i çok kaynaklılık eklenmeden önceki hâliyle aynı
        // kalmalı — mevcut uçların ve panonun davranışı değişmesin.
        var scope = _catalog.BuildScope(new[] { "Satislar" });
        var built = DatasetAggregateQueryBuilder.Build(
            Q(metrics: new[] { new MetricSpec("sum", "tutar") }), scope);

        Assert.DoesNotContain("JOIN", built.Sql);
        Assert.DoesNotContain("d0.", built.Sql);
        Assert.Contains("FROM \"DatasetRows\"\n", built.Sql);

        var rows = await RunAsync(Q(metrics: new[] { new MetricSpec("sum", "tutar") }), scope);
        Assert.Equal(500m, Assert.Single(rows).Values[0]);   // 100+150+200+50
    }

    [Fact]
    public void AmbiguousColumn_IsRejected_NotGuessed()
    {
        // İki sette de bulunan bir kolon adı için rastgele birini seçmek sessiz yanlış
        // cevap demektir. Şu an ortak ad yok, o yüzden yapay bir kapsam kuruyoruz.
        var scope = new QueryScope(new[]
        {
            new QuerySource("d0", _satislarId, "A", new Dictionary<string, string> { ["ortak"] = "text" }),
            new QuerySource("d1", _musterilerId, "B", new Dictionary<string, string> { ["ortak"] = "text" })
        }, new[] { new QueryJoin("d0", "ortak", "d1", "ortak") });

        var ex = Assert.Throws<InvalidQueryException>(() => scope.Resolve("ortak"));
        Assert.Contains("birden çok veri setinde", ex.Message);
    }

    [Fact]
    public void UnrelatedDatasets_AreRejectedWithActionableMessage()
    {
        // İlişki tanımlı değilse birleştirme kurulamaz; mesaj ne yapılması gerektiğini söylemeli.
        var catalog = new TenantCatalog(_catalog.Datasets, Array.Empty<RelationInfo>());
        var scope = catalog.BuildScope(new[] { "Satislar", "Musteriler" });

        var ex = Assert.Throws<InvalidQueryException>(() =>
            DatasetAggregateQueryBuilder.Build(
                Q(metrics: new[] { new MetricSpec("count") }), scope));

        Assert.Contains("ilişki tanımlayın", ex.Message);
    }

    [Fact]
    public void UnknownDataset_ListsAvailableOnes()
    {
        var ex = Assert.Throws<InvalidQueryException>(() =>
            _catalog.BuildScope(new[] { "Faturalar" }));

        Assert.Contains("Satislar", ex.Message);
        Assert.Contains("Musteriler", ex.Message);
    }
}
