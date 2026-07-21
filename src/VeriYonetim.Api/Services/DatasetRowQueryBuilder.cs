using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

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

// JSONB üzerinde dinamik, tip-farkında ve injection'a kapalı sorgu üreten saf builder.
// Saf: DB/HTTP yok — SQL string + parametre üretir, böylece birim test edilebilir.
public static class DatasetRowQueryBuilder
{
    // Karşılaştırma operatörleri → SQL karşılığı (whitelist). "contains" ayrı ele alınır.
    private static readonly Dictionary<string, string> Operators = new()
    {
        ["eq"] = "=", ["ne"] = "<>", ["gt"] = ">", ["gte"] = ">=", ["lt"] = "<", ["lte"] = "<="
    };

    // schema: kolon adı → tip ("text"|"number"|"date"). Hem whitelist hem tip-farkında cast için.
    public static BuiltQuery Build(RowQuery query, IReadOnlyDictionary<string, string> schema)
    {
        var where = new StringBuilder();
        var parameters = new List<NpgsqlParameter>();
        var i = 0;

        foreach (var f in query.Filters)
        {
            if (!schema.TryGetValue(f.Column, out var type))
                throw new InvalidQueryException($"Bilinmeyen kolon: {f.Column}");

            var pName = $"f{i}";

            if (f.Op == "contains")
            {
                // Kısmi metin araması; yalnızca metin kolonlarında anlamlı.
                if (type != "text")
                    throw new InvalidQueryException($"'contains' yalnızca metin kolonlarda kullanılır: {f.Column}");
                where.Append($" AND {TextExpr(f.Column)} ILIKE @{pName}");
                parameters.Add(new NpgsqlParameter(pName, NpgsqlDbType.Text) { Value = $"%{f.Value}%" });
            }
            else if (Operators.TryGetValue(f.Op, out var sqlOp))
            {
                where.Append($" AND {TypedExpr(f.Column, type)} {sqlOp} @{pName}");
                parameters.Add(TypedParam(pName, type, f.Value));
            }
            else
            {
                throw new InvalidQueryException($"Bilinmeyen operatör: {f.Op}");
            }
            i++;
        }

        var orderBy = "";
        if (!string.IsNullOrWhiteSpace(query.Sort))
        {
            if (!schema.TryGetValue(query.Sort, out var sortType))
                throw new InvalidQueryException($"Bilinmeyen sıralama kolonu: {query.Sort}");
            // dir yalnızca asc/desc; başka her şey asc'a düşer (injection'a kapalı).
            var dir = string.Equals(query.Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            orderBy = $" ORDER BY {TypedExpr(query.Sort, sortType)} {dir}";
        }

        return new BuiltQuery(where.ToString(), orderBy, parameters);
    }

    // Tipli ifade: sayı/tarih cast edilerek doğru karşılaştırma ve sıralama sağlanır
    // (aksi halde "100" < "30" gibi metinsel kıyas olurdu).
    private static string TypedExpr(string col, string type) => type switch
    {
        "number" => $"({TextExpr(col)})::numeric",
        "date" => $"({TextExpr(col)})::timestamp",
        _ => TextExpr(col)
    };

    // Ham metin çıkarımı: "Data"->>'col'. Kolon adı SQL'e string literal olarak gömüldüğünden
    // (parametre olamaz) tek tırnaklar ikiye katlanarak escape edilir — whitelist'e ek güvenlik.
    private static string TextExpr(string col) => $"(\"Data\"->>'{col.Replace("'", "''")}')";

    // Değer daima parametre olarak geçer; tipe göre doğru NpgsqlDbType ile. Bozuk değer → 400.
    private static NpgsqlParameter TypedParam(string name, string type, string value)
    {
        switch (type)
        {
            case "number":
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    throw new InvalidQueryException($"'{value}' geçerli bir sayı değil.");
                return new NpgsqlParameter(name, NpgsqlDbType.Numeric) { Value = d };
            case "date":
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    throw new InvalidQueryException($"'{value}' geçerli bir tarih değil.");
                return new NpgsqlParameter(name, NpgsqlDbType.Timestamp) { Value = dt };
            default:
                return new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value };
        }
    }
}
