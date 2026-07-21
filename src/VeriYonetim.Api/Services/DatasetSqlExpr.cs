using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace VeriYonetim.Api.Services;

// JSONB kolonları için ortak, injection'a kapalı SQL ifade üreticileri.
// Hem satır listeleme (DatasetRowQueryBuilder) hem agregasyon (DatasetAggregateQueryBuilder)
// bu yardımcıyı kullanır — tek yerde tutulan üçlü güvenlik: whitelist + parametre + escape.
internal static class DatasetSqlExpr
{
    // Karşılaştırma operatörleri → SQL karşılığı (whitelist). "contains" ayrı ele alınır.
    public static readonly IReadOnlyDictionary<string, string> Operators = new Dictionary<string, string>
    {
        ["eq"] = "=", ["ne"] = "<>", ["gt"] = ">", ["gte"] = ">=", ["lt"] = "<", ["lte"] = "<="
    };

    // Ham metin çıkarımı: "Data"->>'col'. Kolon adı SQL literal'e gömüldüğünden (parametre
    // olamaz) tek tırnaklar ikiye katlanarak escape edilir — whitelist'e ek güvenlik.
    public static string Text(string col) => $"(\"Data\"->>'{col.Replace("'", "''")}')";

    // Tip-farkında ifade: sayı/tarih cast edilerek doğru sayısal/kronolojik kıyas sağlanır
    // (aksi halde "100" < "9" gibi metinsel kıyas olurdu).
    public static string Typed(string col, string type) => type switch
    {
        "number" => $"({Text(col)})::numeric",
        "date" => $"({Text(col)})::timestamp",
        _ => Text(col)
    };

    // Değer daima parametre olarak geçer; tipe göre doğru NpgsqlDbType ile. Bozuk değer → 400.
    public static NpgsqlParameter TypedParam(string name, string type, string value)
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

    // Filtreleri parametreli WHERE ekine (" AND ...") çevirir. Kolon whitelist + değer parametre.
    public static (string Where, List<NpgsqlParameter> Parameters) BuildWhere(
        IReadOnlyList<RowFilter> filters, IReadOnlyDictionary<string, string> schema)
    {
        var where = new StringBuilder();
        var parameters = new List<NpgsqlParameter>();
        var i = 0;

        foreach (var f in filters)
        {
            if (!schema.TryGetValue(f.Column, out var type))
                throw new InvalidQueryException($"Bilinmeyen kolon: {f.Column}");

            var pName = $"f{i}";
            if (f.Op == "contains")
            {
                if (type != "text")
                    throw new InvalidQueryException($"'contains' yalnızca metin kolonlarda kullanılır: {f.Column}");
                where.Append($" AND {Text(f.Column)} ILIKE @{pName}");
                parameters.Add(new NpgsqlParameter(pName, NpgsqlDbType.Text) { Value = $"%{f.Value}%" });
            }
            else if (Operators.TryGetValue(f.Op, out var sqlOp))
            {
                where.Append($" AND {Typed(f.Column, type)} {sqlOp} @{pName}");
                parameters.Add(TypedParam(pName, type, f.Value));
            }
            else
            {
                throw new InvalidQueryException($"Bilinmeyen operatör: {f.Op}");
            }
            i++;
        }

        return (where.ToString(), parameters);
    }
}
