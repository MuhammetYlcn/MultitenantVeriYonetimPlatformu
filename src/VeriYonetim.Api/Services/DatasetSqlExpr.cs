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

    // Filtre ağacının derinlik ve genişlik sınırları. Doğal dilden gelen bir plan bunları
    // aşıyorsa büyük ihtimalle hatalıdır; sınır aynı zamanda özyinelemeyi güvenceye alır.
    private const int MaxFilterDepth = 3;
    private const int MaxGroupChildren = 20;

    // Kolon adı SQL literal'e gömüldüğünden (parametre olamaz) tek tırnaklar ikiye
    // katlanarak escape edilir — whitelist'e ek güvenlik.
    private static string Escape(string name) => name.Replace("'", "''");

    // Ham metin çıkarımı: "Data"->>'col', çok kaynaklı sorguda d1."Data"->>'col'.
    // Takma ad yalnızca gerektiğinde yazılır ki tek veri setli sorgunun SQL'i değişmesin.
    public static string Text(ResolvedColumn column) => column.Alias is null
        ? $"(\"Data\"->>'{Escape(column.Name)}')"
        : $"({column.Alias}.\"Data\"->>'{Escape(column.Name)}')";

    // Tip-farkında ifade: sayı/tarih cast edilerek doğru sayısal/kronolojik kıyas sağlanır
    // (aksi halde "100" < "9" gibi metinsel kıyas olurdu).
    public static string Typed(ResolvedColumn column) => column.Type switch
    {
        "number" => $"({Text(column)})::numeric",
        "date" => $"({Text(column)})::timestamp",
        _ => Text(column)
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

    // Tek veri setli çağrı biçimi (querystring uçları ve mevcut testler).
    public static (string Where, List<NpgsqlParameter> Parameters) BuildWhere(
        IReadOnlyList<FilterNode> filters, IReadOnlyDictionary<string, string> schema) =>
        BuildWhere(filters, QueryScope.Single(schema));

    // Filtreleri parametreli WHERE ekine (" AND ...") çevirir. Kolon whitelist + değer parametre.
    // Listenin elemanları VE ile bağlanır; VEYA için listeye FilterGroup konur.
    public static (string Where, List<NpgsqlParameter> Parameters) BuildWhere(
        IReadOnlyList<FilterNode> filters, QueryScope scope)
    {
        var where = new StringBuilder();
        var parameters = new List<NpgsqlParameter>();
        var next = 0;

        foreach (var f in filters)
        {
            where.Append(" AND ");
            where.Append(BuildNode(f, scope, parameters, ref next, 0));
        }

        return (where.ToString(), parameters);
    }

    // Ağacın bir düğümünü SQL parçasına çevirir; yaprakta BuildLeaf'e iner.
    private static string BuildNode(FilterNode node, QueryScope scope,
        List<NpgsqlParameter> parameters, ref int next, int depth)
    {
        if (depth > MaxFilterDepth)
            throw new InvalidQueryException($"Filtre {MaxFilterDepth} kattan derin olamaz.");

        switch (node)
        {
            case RowFilter leaf:
                return BuildLeaf(leaf, scope, parameters, ref next);

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
                    parts.Add(BuildNode(child, scope, parameters, ref next, depth + 1));

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
    // next: parametre adı sayacı (@f0, @f1…). ref çünkü tek bir koşul birden fazla parametre
    // üretebilir ("inPeriod" başlangıç + bitiş olmak üzere iki tane).
    internal static string BuildLeaf(RowFilter f, QueryScope scope,
        List<NpgsqlParameter> parameters, ref int next)
    {
        var column = scope.Resolve(f.Column);
        var op = (f.Op ?? "").Trim();

        // Değer istemeyen operatörler; parametre de üretmezler (sayaç ilerlemez).
        // Boş hücre içeri alınırken JSON null yazıldığından ->> de NULL döner: "tutarı
        // girilmemiş kayıtlar" sorusunun karşılığı doğrudan IS NULL.
        if (op is "isNull" or "notNull")
            return $"{Text(column)} IS {(op == "isNull" ? "" : "NOT ")}NULL";

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

                parameters.Add(ArrayParam(p, column.Type, values));
                // = ANY(dizi) / <> ALL(dizi): tek koşulda çoklu değer.
                return op == "in"
                    ? $"{Typed(column)} = ANY(@{p})"
                    : $"{Typed(column)} <> ALL(@{p})";
            }

            case "inPeriod":
            {
                if (column.Type != "date")
                    throw new InvalidQueryException(
                        $"'inPeriod' yalnızca tarih kolonlarında kullanılır: {f.Column}");

                // Tarih aritmetiği modele değil sunucuya ait (bkz. RelativePeriod).
                if (!RelativePeriod.TryResolve(f.Value, DateTime.Now, out var start, out var end))
                    throw new InvalidQueryException(
                        $"Bilinmeyen dönem: '{f.Value}'. ({string.Join(", ", RelativePeriod.Tokens)})");

                var p2 = $"f{next++}";
                parameters.Add(new NpgsqlParameter(p, NpgsqlDbType.Timestamp) { Value = start });
                parameters.Add(new NpgsqlParameter(p2, NpgsqlDbType.Timestamp) { Value = end });

                var expr = Typed(column);
                return $"({expr} >= @{p} AND {expr} < @{p2})";
            }

            case "contains":
            {
                if (column.Type != "text")
                    throw new InvalidQueryException(
                        $"'contains' yalnızca metin kolonlarda kullanılır: {f.Column}");

                parameters.Add(new NpgsqlParameter(p, NpgsqlDbType.Text) { Value = $"%{f.Value}%" });
                return $"{Text(column)} ILIKE @{p}";
            }

            default:
            {
                if (!Operators.TryGetValue(op, out var sqlOp))
                    throw new InvalidQueryException($"Bilinmeyen operatör: {f.Op}");

                parameters.Add(TypedParam(p, column.Type, f.Value));
                return $"{Typed(column)} {sqlOp} @{p}";
            }
        }
    }

    // FROM cümlesi + ilk kaynağın veri seti koşulu + veri seti parametreleri.
    //
    // Tek kaynakta takma ad YAZILMAZ ve koşul "DatasetId" = @datasetId olarak kalır —
    // çok kaynaklılık eklenmeden önceki SQL metniyle birebir aynı. Çok kaynakta her set
    // ayrı takma adla katılır ve kendi "DatasetId" koşulunu JOIN'in ON'unda taşır.
    //
    // Veri seti kimlikleri DAİMA parametredir. Sahiplik doğrulaması ayrıca çağıran tarafta
    // yapılır: ham SQL, EF'in global query filter'ını atladığından izolasyonun dayanağı
    // "her datasetId parametre" + "her datasetId tenant'a ait mi kontrolü" ikilisidir.
    public static BuiltFrom BuildFrom(QueryScope scope)
    {
        if (scope.IsSingleSource)
        {
            // Kimlik biliniyorsa parametreyi burada bağlıyoruz. Bilinmiyorsa (querystring
            // ucunun kullandığı QueryScope.Single(schema) yolu) çağıran ekler — o davranış
            // korunuyor ki mevcut uçlar ve testler değişmesin.
            var single = scope.Sources[0].DatasetId == Guid.Empty
                ? Array.Empty<NpgsqlParameter>()
                : new[] { new NpgsqlParameter("datasetId", scope.Sources[0].DatasetId) };

            return new BuiltFrom("\"DatasetRows\"", "\"DatasetId\" = @datasetId", single);
        }

        var parameters = new List<NpgsqlParameter>();
        var sql = new StringBuilder();

        var first = scope.Sources[0];
        sql.Append($"\"DatasetRows\" {first.Alias}");
        parameters.Add(new NpgsqlParameter($"ds_{first.Alias}", first.DatasetId));

        // Bağlar sırayla uygulanır: her yeni kaynak, o ana kadar eklenmiş bir kaynağa bağlanır.
        var joined = new HashSet<string> { first.Alias };

        foreach (var source in scope.Sources.Skip(1))
        {
            var join = scope.Joins.FirstOrDefault(j =>
                (j.RightAlias == source.Alias && joined.Contains(j.LeftAlias)) ||
                (j.LeftAlias == source.Alias && joined.Contains(j.RightAlias)));

            if (join is null)
                throw new InvalidQueryException(
                    $"'{source.Name}' veri setinin diğerlerine nasıl bağlanacağı tanımlı değil. " +
                    "Önce veri setleri arasında ilişki tanımlayın.");

            // Bağ koşulu METİN üzerinden kurulur (Text, Typed değil): aynı anahtar iki
            // sette farklı tiplerde algılanmış olabilir — birinde "1042" metin, diğerinde
            // 1042 sayı. Tipli kıyas o durumda hata bile vermeden hiçbir satırı eşleştirmezdi.
            var left = scope.Resolve($"{join.LeftAlias}.{join.LeftColumn}");
            var right = scope.Resolve($"{join.RightAlias}.{join.RightColumn}");

            parameters.Add(new NpgsqlParameter($"ds_{source.Alias}", source.DatasetId));

            sql.Append($"\nJOIN \"DatasetRows\" {source.Alias}");
            sql.Append($" ON {source.Alias}.\"DatasetId\" = @ds_{source.Alias}");
            sql.Append($" AND {Text(left)} = {Text(right)}");

            joined.Add(source.Alias);
        }

        return new BuiltFrom(sql.ToString(), $"{first.Alias}.\"DatasetId\" = @ds_{first.Alias}",
            parameters);
    }
}
