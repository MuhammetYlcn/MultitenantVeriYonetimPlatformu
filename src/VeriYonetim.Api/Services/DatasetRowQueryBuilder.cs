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
public record RowFilter(string Column, string Op, string Value = "", IReadOnlyList<string>? Values = null)
    : FilterNode;

// Listeleme isteğinin tüm parçaları (sayfalama + sıralama + filtreler).
// Filters düz liste = hepsi VE; araya FilterGroup konarak VEYA kurulabilir.
public record RowQuery(int Page, int PageSize, string? Sort, string? Dir, IReadOnlyList<FilterNode> Filters);

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
