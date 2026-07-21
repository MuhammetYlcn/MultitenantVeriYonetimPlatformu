using Npgsql;

namespace VeriYonetim.Api.Services;

// Tek bir filtre koşulu: kolon, operatör ("eq"/"gte"/"contains"…), değer (ham string).
public record RowFilter(string Column, string Op, string Value);

// Listeleme isteğinin tüm parçaları (sayfalama + sıralama + filtreler).
public record RowQuery(int Page, int PageSize, string? Sort, string? Dir, IReadOnlyList<RowFilter> Filters);

// Build çıktısı: parametreli WHERE eki (" AND ..."), ORDER BY eki ve Npgsql parametreleri.
// datasetId / limit / offset gibi sabitler bu builder'da DEĞİL, çağıran tarafta eklenir.
public record BuiltQuery(string WhereSql, string OrderBySql, IReadOnlyList<NpgsqlParameter> Parameters);

// Geçersiz kolon/operatör/değer için: çağıran bunu 400'e çevirir.
public class InvalidQueryException(string message) : Exception(message);

// JSONB üzerinde dinamik, tip-farkında ve injection'a kapalı satır sorgusu üreten saf builder.
// Saf: DB/HTTP yok — SQL string + parametre üretir, böylece birim test edilebilir.
// Ortak SQL ifade üretimi DatasetSqlExpr'de (agregasyon builder'ı da onu paylaşır).
public static class DatasetRowQueryBuilder
{
    // schema: kolon adı → tip ("text"|"number"|"date"). Hem whitelist hem tip-farkında cast için.
    public static BuiltQuery Build(RowQuery query, IReadOnlyDictionary<string, string> schema)
    {
        var (where, parameters) = DatasetSqlExpr.BuildWhere(query.Filters, schema);

        var orderBy = "";
        if (!string.IsNullOrWhiteSpace(query.Sort))
        {
            if (!schema.TryGetValue(query.Sort, out var sortType))
                throw new InvalidQueryException($"Bilinmeyen sıralama kolonu: {query.Sort}");
            // dir yalnızca asc/desc; başka her şey asc'a düşer (injection'a kapalı).
            var dir = string.Equals(query.Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            orderBy = $" ORDER BY {DatasetSqlExpr.Typed(query.Sort, sortType)} {dir}";
        }

        return new BuiltQuery(where, orderBy, parameters);
    }
}
