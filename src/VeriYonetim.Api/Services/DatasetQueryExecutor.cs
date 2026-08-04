using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;

namespace VeriYonetim.Api.Services;

// Builder'ların ürettiği ham SQL'i çalıştırır.
//
// Ayrı bir servis çünkü artık iki uç (querystring /aggregate ve doğal dil /ask) aynı SQL'i
// çalıştırıyor ve okuma mantığı (sütun düzeni, NULL ele alma) tek yerde durmalı. Sütun
// sırasını BuiltAggregate taşıdığı için buradaki okuma tahmine dayanmıyor.
public interface IDatasetQueryExecutor
{
    Task<IReadOnlyList<AggregateBucket>> RunAggregateAsync(
        BuiltAggregate built, IReadOnlyList<NpgsqlParameter>? extra = null, CancellationToken ct = default);

    Task<IReadOnlyList<string?[]>> RunRowsAsync(BuiltRowSelect built, CancellationToken ct = default);
}

public class DatasetQueryExecutor : IDatasetQueryExecutor
{
    private readonly AppDbContext _db;

    public DatasetQueryExecutor(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<AggregateBucket>> RunAggregateAsync(
        BuiltAggregate built, IReadOnlyList<NpgsqlParameter>? extra = null, CancellationToken ct = default)
    {
        var buckets = new List<AggregateBucket>();

        await ReadAsync(built.Sql, Combine(built.Parameters, extra), async reader =>
        {
            var keys = new string?[built.KeyCount];
            for (var i = 0; i < built.KeyCount; i++)
                keys[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetString(i);

            var values = new decimal?[built.MetricCount];
            for (var i = 0; i < built.MetricCount; i++)
            {
                var ordinal = built.KeyCount + i;
                values[i] = await reader.IsDBNullAsync(ordinal, ct) ? null : reader.GetDecimal(ordinal);
            }

            var countOrdinal = built.KeyCount + built.MetricCount;
            decimal? share = built.HasShare && !await reader.IsDBNullAsync(countOrdinal + 1, ct)
                ? reader.GetDecimal(countOrdinal + 1)
                : null;

            buckets.Add(new AggregateBucket(keys, values, reader.GetInt32(countOrdinal), share));
        }, ct);

        return buckets;
    }

    public async Task<IReadOnlyList<string?[]>> RunRowsAsync(
        BuiltRowSelect built, CancellationToken ct = default)
    {
        var rows = new List<string?[]>();
        var width = built.Columns.Count;

        await ReadAsync(built.Sql, built.Parameters, async reader =>
        {
            var values = new string?[width];
            for (var i = 0; i < width; i++)
                values[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetString(i);

            rows.Add(values);
            await Task.CompletedTask;
        }, ct);

        return rows;
    }

    // Ortak okuma döngüsü: bağlantıyı açar (açıksa dokunmaz), parametreleri bağlar, satırları okur.
    private async Task ReadAsync(string sql, IReadOnlyList<NpgsqlParameter> parameters,
        Func<NpgsqlDataReader, Task> readRow, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var p in parameters) command.Parameters.Add(p);

        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(ct);

        try
        {
            await using var reader = (NpgsqlDataReader)await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                await readRow(reader);
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }
    }

    private static List<NpgsqlParameter> Combine(
        IReadOnlyList<NpgsqlParameter> first, IReadOnlyList<NpgsqlParameter>? second)
    {
        var all = new List<NpgsqlParameter>(first);
        if (second is not null) all.AddRange(second);
        return all;
    }
}
