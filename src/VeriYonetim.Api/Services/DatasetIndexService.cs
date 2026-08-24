using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

// Kolon indekslemesinin sonucu. Kurulamayan bir indeks hata değil, açıklanması gereken
// bir cevaptır: kullanıcı neden hızlanmadığını görmeli.
public record IndexResult(bool Created, string? Reason, double Seconds);

public interface IDatasetIndexService
{
    Task<IndexResult> CreateAsync(Guid datasetId, string columnName, CancellationToken ct = default);
    Task<bool> DropAsync(Guid datasetId, string columnName, CancellationToken ct = default);
    Task<HashSet<string>> IndexedColumnsAsync(Guid datasetId, CancellationToken ct = default);
}

// Kolon bazlı ifade indekslerini kurar ve düşürür.
//
// İndekslenen ifade, sorgunun ürettiği ifadeyle KARAKTER KARAKTER aynı olmalıdır; yoksa
// PostgreSQL indeksi hiç kullanmaz ve kullanıcı hızlandırdığını sanır. Bu yüzden
// ifadeler DatasetSqlExpr'in ürettiklerinden türetildi (metin karşılaştırması `lower()`
// altında yapılıyor, sayısal karşılaştırma `::numeric` altında).
public class DatasetIndexService : IDatasetIndexService
{
    private readonly AppDbContext _db;
    private readonly ILogger<DatasetIndexService> _logger;

    public DatasetIndexService(AppDbContext db, ILogger<DatasetIndexService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IndexResult> CreateAsync(
        Guid datasetId, string columnName, CancellationToken ct = default)
    {
        var column = await _db.DatasetColumns
            .Where(c => c.DatasetId == datasetId && c.Name == columnName)
            .Select(c => new { c.Name, c.Type })
            .FirstOrDefaultAsync(ct);

        if (column is null)
            return new IndexResult(false, $"'{columnName}' bu veri setinde yok.", 0);

        // Tarih kolonları indekslenemiyor ve bu bir eksiklik değil, PostgreSQL'in
        // kuralı: metinden tarihe dönüşüm DateStyle ayarına bağlı olduğu için IMMUTABLE
        // sayılmıyor, IMMUTABLE olmayan ifade de indekslenemiyor. 24.08 ölçümünde bu
        // aday denendi ve tam olarak bu hatayla düştü. Kullanıcıya sessizce "kuruldu"
        // demektense sebebi söyleniyor.
        if (Expression(column.Name, column.Type) is not { } expression)
            return new IndexResult(false,
                column.Type == "date"
                    ? "Tarih kolonları indekslenemiyor: metinden tarihe dönüşüm " +
                      "veritabanı ayarına bağlı olduğundan indekse konamaz."
                    : $"'{column.Type}' tipi indekslenmiyor.",
                0);

        if (await _db.DatasetIndexes
                .AnyAsync(i => i.DatasetId == datasetId && i.ColumnName == column.Name, ct))
            return new IndexResult(false, "Bu kolon zaten indeksli.", 0);

        // TİP ÇAKIŞMASI. Satırlar tek bir tabloda durduğu için indeks de tablo
        // genelindedir: `tutar` kolonunu sayı olarak indekslemek, tabloda `tutar` adını
        // taşıyan BÜTÜN satırların sayıya çevrilmesini gerektirir. Aynı adı metin kolon
        // olarak kullanan başka bir veri seti varsa (ki değerleri "1.500,50" gibi
        // olabilir) indeks kurulurken veritabanı hata verir.
        //
        // Bu, denenmeden görülmeyen bir kusurdu: ölçüm ortamında her set aynı şemadan
        // türetildiği için hiç çıkmadı, gerçek veride ilk denemede çıktı. Kontrol burada
        // ve şema üzerinden yapılıyor — hem ucuz hem de sebebi söylenebilir.
        var conflicting = await _db.DatasetColumns
            .IgnoreQueryFilters()
            .Where(c => c.Name == column.Name && c.Type != column.Type)
            .Select(c => c.Type)
            .Distinct()
            .ToListAsync(ct);

        if (conflicting.Count > 0)
            return new IndexResult(false,
                $"'{column.Name}' adı başka bir veri setinde farklı tipte " +
                $"({string.Join(", ", conflicting)}) kullanılıyor. Satırlar tek bir " +
                "tabloda durduğundan indeks de ortaktır; aynı ad iki farklı tiple " +
                "indekslenemiyor.", 0);

        var indexName = IndexName(column.Name, column.Type);

        // Fiziksel indeks başka bir setin kaydı yüzünden zaten duruyor olabilir; o zaman
        // yalnız kayıt eklenir. IF NOT EXISTS bunu veritabanına da doğrulatıyor.
        // Önceki bir denemeden kalmış GEÇERSİZ indeks varsa önce o temizlenir. Aksi
        // halde aşağıdaki IF NOT EXISTS onu "zaten var" sayıp atlar, kayıt eklenir ve
        // kullanıcı hızlandırdığını sanar — oysa sorgular o indeksi kullanamaz.
        await DropIfInvalidAsync(indexName, ct);

        var started = DateTime.UtcNow;

        try
        {
            await CreateIndexAsync(indexName, expression, ct);
        }
        catch (PostgresException ex)
        {
            // CREATE INDEX CONCURRENTLY başarısız olduğunda arkasında geçersiz bir
            // indeks bırakır; temizlenmezse bir sonraki deneme onu var sayar.
            await DropIfInvalidAsync(indexName, ct);

            if (ex.SqlState != PostgresErrorCodes.InvalidTextRepresentation) throw;

            // Şema kontrolünü geçmiş ama satırda yine de çevrilemeyen bir değer var:
            // şemanın söylediğiyle satırda duranın ayrıştığı tek yer burası olabilir.
            // İsteği çökertmek yerine ne bulunduğu söyleniyor.
            _logger.LogWarning(ex,
                "Kolon indekslenemedi, veri tipe uymuyor: {Column}", column.Name);

            return new IndexResult(false,
                $"'{column.Name}' kolonunda {column.Type} tipine çevrilemeyen bir değer " +
                $"var: {ex.MessageText}", 0);
        }

        var seconds = (DateTime.UtcNow - started).TotalSeconds;

        _db.DatasetIndexes.Add(new DatasetIndex
        {
            Id = Guid.NewGuid(),
            DatasetId = datasetId,
            ColumnName = column.Name,
            ColumnType = column.Type,
            IndexName = indexName
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Kolon indeksi kuruldu: {Column} ({Type}) → {Index}, {Seconds:F1} sn",
            column.Name, column.Type, indexName, seconds);

        return new IndexResult(true, null, seconds);
    }

    public async Task<bool> DropAsync(
        Guid datasetId, string columnName, CancellationToken ct = default)
    {
        var record = await _db.DatasetIndexes
            .FirstOrDefaultAsync(i => i.DatasetId == datasetId && i.ColumnName == columnName, ct);

        if (record is null) return false;

        _db.DatasetIndexes.Remove(record);
        await _db.SaveChangesAsync(ct);

        await DropIfUnusedAsync(record.IndexName, ct);

        return true;
    }

    public async Task<HashSet<string>> IndexedColumnsAsync(
        Guid datasetId, CancellationToken ct = default) =>
        (await _db.DatasetIndexes
            .Where(i => i.DatasetId == datasetId)
            .Select(i => i.ColumnName)
            .ToListAsync(ct))
        .ToHashSet();

    // Fiziksel indeksi yalnız SON kayıt gidince düşür: aynı kolon adını kullanan başka
    // bir veri seti — başka bir firmanınki de olabilir — hâlâ ondan faydalanıyor olabilir.
    // Query filter bilinçli olarak atlanıyor; sorulan şey "benim setlerim" değil,
    // "bu indekse dayanan başka kayıt kaldı mı".
    private async Task DropIfUnusedAsync(string indexName, CancellationToken ct)
    {
        var stillUsed = await _db.DatasetIndexes
            .IgnoreQueryFilters()
            .AnyAsync(i => i.IndexName == indexName, ct);

        if (stillUsed) return;

        await ExecuteOutsideTransactionAsync($"DROP INDEX CONCURRENTLY IF EXISTS {indexName}", ct);
        _logger.LogInformation("Kolon indeksi düşürüldü: {Index}", indexName);
    }

    // Yarım kalmış bir indeksi düşürür. PostgreSQL, CONCURRENTLY kurulumu başarısız
    // olduğunda indeksi silmez; `indisvalid = false` olarak bırakır. Böyle bir indeks
    // sorgularda kullanılmaz ama adı doludur — yani "zaten var" görünür.
    private async Task DropIfInvalidAsync(string indexName, CancellationToken ct)
    {
        var connectionString = _db.Database.GetConnectionString();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText =
                """
                SELECT COUNT(*) FROM pg_index i
                JOIN pg_class c ON c.oid = i.indexrelid
                WHERE c.relname = @name AND NOT i.indisvalid
                """;
            check.Parameters.AddWithValue("name", indexName);

            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) == 0) return;
        }

        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP INDEX IF EXISTS {indexName}";
        drop.CommandTimeout = 1800;
        await drop.ExecuteNonQueryAsync(ct);

        _logger.LogWarning("Yarım kalmış indeks temizlendi: {Index}", indexName);
    }

    private Task CreateIndexAsync(string indexName, string expression, CancellationToken ct) =>
        ExecuteOutsideTransactionAsync(
            $"""CREATE INDEX CONCURRENTLY IF NOT EXISTS {indexName} ON "DatasetRows" ("DatasetId", {expression})""",
            ct);

    // CONCURRENTLY, indeks kurulurken tabloya YAZILABİLMESİNİ sağlar. Bu kaçınılmaz:
    // satırlar tek bir tabloda durduğundan, düz bir CREATE INDEX o süre boyunca
    // BÜTÜN firmaların içe aktarmasını bekletirdi (2,2 milyon satırlık ölçüm tablosunda
    // 1,7-17 saniye). Bedeli, komutun bir işlemin içinde çalışamaması — bu yüzden
    // kendi bağlantısını açıyor.
    private async Task ExecuteOutsideTransactionAsync(string sql, CancellationToken ct)
    {
        var connectionString = _db.Database.GetConnectionString();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1800;
        await command.ExecuteNonQueryAsync(ct);
    }

    // İndekslenecek ifade. Tip desteklenmiyorsa null.
    private static string? Expression(string column, string type) => type switch
    {
        // Metin eşitliği harfe duyarsız yapılıyor (DatasetSqlExpr'deki karar); indeks de
        // aynı ifadeye kurulmalı, ham değere kurulan indeks o sorguda kullanılmaz.
        "text" => $"lower((\"Data\"->>'{Escape(column)}'))",
        "number" => $"(((\"Data\"->>'{Escape(column)}'))::numeric)",
        _ => null
    };

    // İndeks adı kolon adından TÜRETİLMEZ, özetlenir. Sebebi iki katlı: PostgreSQL
    // tanımlayıcıları 63 baytla sınırlı (uzun ya da Türkçe karakterli bir kolon adı
    // sessizce kırpılıp iki kolonu aynı ada düşürebilirdi), ve ad SQL metnine gömüldüğü
    // için kolon adından gelen hiçbir karakterin oraya ulaşmaması gerekir.
    private static string IndexName(string column, string type)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{type}:{column}"));
        return $"ix_rows_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    // İfadenin içindeki kolon adı parametre olamaz (indeks tanımının parçası), bu yüzden
    // tek tırnaklar ikiye katlanıyor — RelationDetector'daki profilleme sorgusuyla aynı
    // kural. Kolon adının kendisi ayrıca şemadan doğrulanarak geliyor, uydurulmuş bir
    // ad buraya kadar ulaşamaz.
    private static string Escape(string name) => name.Replace("'", "''");
}
