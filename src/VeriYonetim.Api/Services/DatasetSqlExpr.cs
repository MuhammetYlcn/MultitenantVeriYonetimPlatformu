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
    // Karşılaştırma operatörleri → SQL karşılığı (whitelist). Çoklu değer ("in"/"notIn"),
    // boşluk ("isNull"/"notNull"), dönem ("inPeriod") ve "contains" ayrı ele alınır.
    public static readonly IReadOnlyDictionary<string, string> Operators = new Dictionary<string, string>
    {
        ["eq"] = "=", ["ne"] = "<>", ["gt"] = ">", ["gte"] = ">=", ["lt"] = "<", ["lte"] = "<="
    };

    // "in" listesine üst sınır: doğal dilden gelen bir sorgunun binlerce değer taşıması
    // beklenmez; sınır hem hatalı planı erken yakalar hem de sorguyu şişmekten korur.
    private const int MaxInValues = 200;

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

    // Metni şema tipine göre çözer. Bozuk değer → 400 (InvalidQueryException).
    // Tek değerli ve çoklu değerli operatörler aynı çözümü paylaşsın diye ayrıldı.
    private static decimal ParseNumber(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new InvalidQueryException($"'{value}' geçerli bir sayı değil.");

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Unspecified)
            : throw new InvalidQueryException($"'{value}' geçerli bir tarih değil.");

    // Değer daima parametre olarak geçer; tipe göre doğru NpgsqlDbType ile.
    public static NpgsqlParameter TypedParam(string name, string type, string value) => type switch
    {
        "number" => new NpgsqlParameter(name, NpgsqlDbType.Numeric) { Value = ParseNumber(value) },
        "date" => new NpgsqlParameter(name, NpgsqlDbType.Timestamp) { Value = ParseDate(value) },
        _ => new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value }
    };

    // Çoklu değer için dizi parametresi. Dizi de PARAMETRE olarak gider; değerlerin hiçbiri
    // SQL metnine gömülmez, yani "in" listesi injection yüzeyi açmaz.
    private static NpgsqlParameter ArrayParam(string name, string type, IReadOnlyList<string> values) => type switch
    {
        "number" => new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Numeric)
        { Value = values.Select(ParseNumber).ToArray() },
        "date" => new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Timestamp)
        { Value = values.Select(ParseDate).ToArray() },
        _ => new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text)
        { Value = values.ToArray() }
    };

    // Filtre ağacının derinlik ve genişlik sınırları. Doğal dilden gelen bir plan bunları
    // aşıyorsa büyük ihtimalle hatalıdır; sınır aynı zamanda özyinelemeyi güvenceye alır.
    private const int MaxFilterDepth = 3;
    private const int MaxGroupChildren = 20;

    // Filtreleri parametreli WHERE ekine (" AND ...") çevirir. Kolon whitelist + değer parametre.
    // Listenin elemanları VE ile bağlanır; VEYA için listeye FilterGroup konur.
    public static (string Where, List<NpgsqlParameter> Parameters) BuildWhere(
        IReadOnlyList<FilterNode> filters, IReadOnlyDictionary<string, string> schema)
    {
        var where = new StringBuilder();
        var parameters = new List<NpgsqlParameter>();
        var next = 0;

        foreach (var f in filters)
        {
            where.Append(" AND ");
            where.Append(BuildNode(f, schema, parameters, ref next, 0));
        }

        return (where.ToString(), parameters);
    }

    // Ağacın bir düğümünü SQL parçasına çevirir; yaprakta BuildLeaf'e iner.
    private static string BuildNode(FilterNode node, IReadOnlyDictionary<string, string> schema,
        List<NpgsqlParameter> parameters, ref int next, int depth)
    {
        if (depth > MaxFilterDepth)
            throw new InvalidQueryException($"Filtre {MaxFilterDepth} kattan derin olamaz.");

        switch (node)
        {
            case RowFilter leaf:
                return BuildLeaf(leaf, schema, parameters, ref next);

            case FilterGroup group:
            {
                var logic = (group.Logic ?? "").Trim().ToLowerInvariant();
                if (logic is not ("and" or "or"))
                    throw new InvalidQueryException($"Bilinmeyen filtre bağlacı: {group.Logic}. (and/or)");

                var children = group.Children ?? Array.Empty<FilterNode>();
                if (children.Count == 0)
                    throw new InvalidQueryException("Filtre grubu boş olamaz.");
                if (children.Count > MaxGroupChildren)
                    throw new InvalidQueryException(
                        $"Bir filtre grubunda en fazla {MaxGroupChildren} koşul olabilir.");

                var parts = new List<string>(children.Count);
                foreach (var child in children)
                    parts.Add(BuildNode(child, schema, parameters, ref next, depth + 1));

                // Parantez ŞART: "a AND (b OR c)" ile "a AND b OR c" farklı sorulardır
                // (AND, OR'dan önce bağlar) ve ikincisi sessizce yanlış sonuç verir.
                return $"({string.Join(logic == "and" ? " AND " : " OR ", parts)})";
            }

            default:
                throw new InvalidQueryException("Bilinmeyen filtre düğümü.");
        }
    }

    // Tek bir filtre koşulunu SQL parçasına çevirir.
    //
    // Ayrı bir metod olmasının sebebi: koşul mantığı tek yerde kalsın. Düz filtre listesi
    // (buradaki BuildWhere) ve ileride VE/VEYA ağacı, yapraklarında ikisi de bunu çağırır.
    //
    // next: parametre adı sayacı (@f0, @f1…). ref çünkü tek bir koşul birden fazla parametre
    // üretebilir ("inPeriod" başlangıç + bitiş olmak üzere iki tane).
    internal static string BuildLeaf(RowFilter f, IReadOnlyDictionary<string, string> schema,
        List<NpgsqlParameter> parameters, ref int next)
    {
        if (!schema.TryGetValue(f.Column, out var type))
            throw new InvalidQueryException($"Bilinmeyen kolon: {f.Column}");

        var op = (f.Op ?? "").Trim();

        // Değer istemeyen operatörler; parametre de üretmezler (sayaç ilerlemez).
        // Boş hücre içeri alınırken JSON null yazıldığından ->> de NULL döner: "tutarı
        // girilmemiş kayıtlar" sorusunun karşılığı doğrudan IS NULL.
        if (op is "isNull" or "notNull")
            return $"{Text(f.Column)} IS {(op == "isNull" ? "" : "NOT ")}NULL";

        var p = $"f{next++}";

        switch (op)
        {
            case "in":
            case "notIn":
            {
                var values = f.Values ?? Array.Empty<string>();
                if (values.Count == 0)
                    throw new InvalidQueryException($"'{op}' için en az bir değer gerekli: {f.Column}");
                if (values.Count > MaxInValues)
                    throw new InvalidQueryException($"'{op}' en fazla {MaxInValues} değer alabilir.");

                parameters.Add(ArrayParam(p, type, values));
                // = ANY(dizi) / <> ALL(dizi): tek koşulda çoklu değer.
                return op == "in"
                    ? $"{Typed(f.Column, type)} = ANY(@{p})"
                    : $"{Typed(f.Column, type)} <> ALL(@{p})";
            }

            case "inPeriod":
            {
                if (type != "date")
                    throw new InvalidQueryException(
                        $"'inPeriod' yalnızca tarih kolonlarında kullanılır: {f.Column}");

                // Tarih aritmetiği modele değil sunucuya ait (bkz. RelativePeriod).
                if (!RelativePeriod.TryResolve(f.Value, DateTime.Now, out var start, out var end))
                    throw new InvalidQueryException(
                        $"Bilinmeyen dönem: '{f.Value}'. ({string.Join(", ", RelativePeriod.Tokens)})");

                var p2 = $"f{next++}";
                parameters.Add(new NpgsqlParameter(p, NpgsqlDbType.Timestamp) { Value = start });
                parameters.Add(new NpgsqlParameter(p2, NpgsqlDbType.Timestamp) { Value = end });

                var expr = Typed(f.Column, "date");
                return $"({expr} >= @{p} AND {expr} < @{p2})";
            }

            case "contains":
            {
                if (type != "text")
                    throw new InvalidQueryException(
                        $"'contains' yalnızca metin kolonlarda kullanılır: {f.Column}");

                parameters.Add(new NpgsqlParameter(p, NpgsqlDbType.Text) { Value = $"%{f.Value}%" });
                return $"{Text(f.Column)} ILIKE @{p}";
            }

            default:
            {
                if (!Operators.TryGetValue(op, out var sqlOp))
                    throw new InvalidQueryException($"Bilinmeyen operatör: {f.Op}");

                parameters.Add(TypedParam(p, type, f.Value));
                return $"{Typed(f.Column, type)} {sqlOp} @{p}";
            }
        }
    }
}
