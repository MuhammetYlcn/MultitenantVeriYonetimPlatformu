using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Services;

public interface IDatasetRowWriter
{
    /// Bir veri setinin satırlarının TAMAMINI verilenlerle değiştirir (import semantiği).
    /// Silme ve yazma tek işlemde olur: yarıda kalırsa set eski hâlinde kalır.
    Task ReplaceRowsAsync(
        Guid datasetId, IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken ct = default);
}

// Satırların toplu yazımı.
//
// Neden ayrı bir sınıf ve neden EF değil: 21.08 ölçümü, içe aktarmanın EF yoluyla
// ~18.000 satır/sn yazdığını, PostgreSQL'in `COPY` akışının ise ~60-100.000 satır/sn
// yazdığını gösterdi (`olcumler/2026-08-21 taban olcum (indekssiz).md`). Aradaki fark
// EF'in her satır için bir varlık nesnesi kurup değişiklik takibine sokmasından geliyor;
// içe aktarmada bu takibin hiçbir karşılığı yok, çünkü satırlar okunmadan yazılıyor.
//
// `COPY` satırları tek tek akışa yazar: 100.000 satırlık bir dosya için sunucuda
// 100.000 varlık nesnesi birikmez.
public class DatasetRowWriter : IDatasetRowWriter
{
    // AppDbContext'teki ValueConverter'ın kullandığı seçeneklerle AYNI olmalı: aynı
    // sözlük iki yoldan da aynı JSON metnine dönmeli, yoksa EF ile yazılan satırla
    // COPY ile yazılan satır farklı biçimde saklanır ve sorgular birinde tutup
    // diğerinde tutmaz (tarih biçimi, ondalık ayracı).
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly AppDbContext _db;

    public DatasetRowWriter(AppDbContext db) => _db = db;

    public async Task ReplaceRowsAsync(
        Guid datasetId, IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken ct = default)
    {
        // Eski satırlar tek komutla gidiyor: önceki yol hepsini belleğe okuyup
        // varlık olarak siliyordu, yani 1 milyon satırlık bir seti değiştirmek için
        // önce 1 milyon satırı okuması gerekiyordu.
        //
        // ExecuteDelete query filter'ı uygular; başka bir firmanın satırına dokunamaz.
        await _db.DatasetRows.Where(r => r.DatasetId == datasetId).ExecuteDeleteAsync(ct);

        if (rows.Count == 0) return;

        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();

        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(ct);

        try
        {
            await using var writer = await connection.BeginBinaryImportAsync(
                """COPY "DatasetRows" ("Id", "Data", "DatasetId") FROM STDIN (FORMAT BINARY)""",
                ct);

            foreach (var row in rows)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(
                    JsonSerializer.Serialize(row, JsonOptions), NpgsqlDbType.Jsonb, ct);
                await writer.WriteAsync(datasetId, NpgsqlDbType.Uuid, ct);
            }

            await writer.CompleteAsync(ct);
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
    }
}
