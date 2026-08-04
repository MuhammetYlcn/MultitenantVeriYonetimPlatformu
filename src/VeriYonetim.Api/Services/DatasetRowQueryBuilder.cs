using Npgsql;

namespace VeriYonetim.Api.Services;

// Filtre ağacının ortak tabanı.
//
// Filtreler ÇOĞUNLUKLA düz bir listedir ve hepsi VE ile bağlanır — "Ankara'daki, 1000 TL
// üstü satışlar". Ağaç yalnızca VEYA gerektiğinde devreye girer: "Ankara'daki VEYA 1000 TL
// üstü satışlar" düz listeyle ifade edilemez. Bu yüzden düz liste hâlâ birinci sınıf; ağaç
// ise aynı listenin içine bir FilterGroup düğümü konarak kurulur.
public abstract record FilterNode;

// Çocuklarını VE ya da VEYA ile bağlayan düğüm. Logic: "and" | "or".
public record FilterGroup(string Logic, IReadOnlyList<FilterNode> Children) : FilterNode;

// Tek bir filtre koşulu: kolon, operatör ("eq"/"gte"/"contains"…), değer (ham string).
//
// Values yalnızca çoklu değer alan operatörler ("in"/"notIn") içindir. Neden ayrı bir
// alan? "Ankara ve İzmir" sorusu tek değerli eq'larla kurulamaz: filtreler VE ile
// bağlandığından "sehir=Ankara AND sehir=İzmir" hiçbir satır döndürmez — üstelik hata da
// vermez. Sessiz yanlış cevabı önlemek için çoklu değer ilk sınıf vatandaş.
//
// Değer istemeyen operatörlerde ("isNull"/"notNull") Value boş kalır; "inPeriod"de ise
// Value bir tarih değil, RelativePeriod etiketidir ("gecenAy").
//
// Column çok kaynaklı sorguda nitelikli olabilir: "musteriler.sehir".
public record RowFilter(string Column, string Op, string Value = "", IReadOnlyList<string>? Values = null)
    : FilterNode;

// Listeleme isteğinin tüm parçaları (sayfalama + sıralama + filtreler).
// Filters düz liste = hepsi VE; araya FilterGroup konarak VEYA kurulabilir.
public record RowQuery(int Page, int PageSize, string? Sort, string? Dir, IReadOnlyList<FilterNode> Filters);

// Build çıktısı: parametreli WHERE eki (" AND ..."), ORDER BY eki ve Npgsql parametreleri.
// datasetId / limit / offset gibi sabitler bu builder'da DEĞİL, çağıran tarafta eklenir.
public record BuiltQuery(string WhereSql, string OrderBySql, IReadOnlyList<NpgsqlParameter> Parameters);

// Çok kaynaklı satır listesi: tam SQL + parametreler + dönen kolonların görünen adları.
// Kolon adları döndürülüyor çünkü JOIN'li sonuçta sütunlar iki farklı veri setinden gelir
// ve okuyucunun hangisinin ne olduğunu bilmesi gerekir.
public record BuiltRowSelect(
    string Sql,
    IReadOnlyList<NpgsqlParameter> Parameters,
    IReadOnlyList<string> Columns);

// Geçersiz kolon/operatör/değer için: çağıran bunu 400'e çevirir.
public class InvalidQueryException(string message) : Exception(message);

// JSONB üzerinde dinamik, tip-farkında ve injection'a kapalı satır sorgusu üreten saf builder.
// Saf: DB/HTTP yok — SQL string + parametre üretir, böylece birim test edilebilir.
// Ortak SQL ifade üretimi DatasetSqlExpr'de (agregasyon builder'ı da onu paylaşır).
public static class DatasetRowQueryBuilder
{
    // Tek veri setli çağrı biçimi (querystring ucu ve mevcut testler).
    // schema: kolon adı → tip ("text"|"number"|"date"). Hem whitelist hem tip-farkında cast için.
    public static BuiltQuery Build(RowQuery query, IReadOnlyDictionary<string, string> schema) =>
        Build(query, QueryScope.Single(schema));

    public static BuiltQuery Build(RowQuery query, QueryScope scope)
    {
        var (where, parameters) = DatasetSqlExpr.BuildWhere(query.Filters, scope);

        return new BuiltQuery(where, BuildOrderBy(query, scope), parameters);
    }

    // Doğal dil sorgularının satır listesi yolu. Burada tam SQL üretilir çünkü çok kaynaklı
    // sonuç bir entity'ye materialize edilemez: sütunlar birden çok veri setinden gelir.
    //
    // columns null ise bütün kaynakların bütün kolonları döner.
    public static BuiltRowSelect BuildSelect(
        RowQuery query, QueryScope scope, IReadOnlyList<string>? columns = null)
    {
        var requested = columns is { Count: > 0 } ? columns : AllColumns(scope);

        var projections = new List<string>(requested.Count);
        var names = new List<string>(requested.Count);

        for (var i = 0; i < requested.Count; i++)
        {
            var column = scope.Resolve(requested[i]);
            // Değerler metin olarak çekilir: sütunlar farklı tiplerde olabilir ve okuyucu
            // tek tip üzerinden ilerlesin. Görünen ad ayrı listede taşınıyor (aşağıda).
            projections.Add($"{DatasetSqlExpr.Text(column)} AS \"c{i}\"");
            names.Add(requested[i]);
        }

        var from = DatasetSqlExpr.BuildFrom(scope);
        var (where, whereParams) = DatasetSqlExpr.BuildWhere(query.Filters, scope);

        var parameters = new List<NpgsqlParameter>(from.Parameters);
        parameters.AddRange(whereParams);

        var limit = Math.Clamp(query.PageSize, 1, 1000);
        var offset = Math.Max(query.Page - 1, 0) * limit;

        var sql = $"""
            SELECT {string.Join(", ", projections)}
            FROM {from.FromSql}
            WHERE {from.BaseWhere}{where}{BuildOrderBy(query, scope)}
            LIMIT {limit} OFFSET {offset}
            """;

        return new BuiltRowSelect(sql, parameters, names);
    }

    private static string BuildOrderBy(RowQuery query, QueryScope scope)
    {
        if (string.IsNullOrWhiteSpace(query.Sort)) return "";

        var column = scope.Resolve(query.Sort);

        // dir yalnızca asc/desc; başka her şey asc'a düşer (injection'a kapalı).
        var dir = string.Equals(query.Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";

        return $" ORDER BY {DatasetSqlExpr.Typed(column)} {dir}";
    }

    // Çok kaynakta kolon adları nitelikli döner ("musteriler.sehir"): aynı ad iki sette
    // birden geçebilir ve çözümleme belirsiz kalırdı.
    private static List<string> AllColumns(QueryScope scope) => scope.Sources
        .SelectMany(s => s.Columns.Keys.Select(c => scope.IsSingleSource ? c : $"{s.Name}.{c}"))
        .ToList();
}
