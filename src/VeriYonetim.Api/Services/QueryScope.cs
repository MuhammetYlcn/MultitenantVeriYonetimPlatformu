using Npgsql;

namespace VeriYonetim.Api.Services;

// FROM cümlesi üretiminin çıktısı: tablo/JOIN metni, ilk kaynağın veri seti koşulu
// (WHERE'in ilk parçası) ve veri seti kimliği parametreleri.
internal record BuiltFrom(
    string FromSql,
    string BaseWhere,
    IReadOnlyList<NpgsqlParameter> Parameters);

// Sorguya katılan tek bir veri seti.
// Alias, SQL'de kullanılan takma addır (d0, d1…); Name kullanıcının/modelin gördüğü addır.
public record QuerySource(
    string Alias,
    Guid DatasetId,
    string Name,
    IReadOnlyDictionary<string, string> Columns);

// İki kaynağı birbirine bağlayan koşul (JOIN ... ON).
public record QueryJoin(string LeftAlias, string LeftColumn, string RightAlias, string RightColumn);

// Çözümlenmiş kolon: hangi kaynakta, hangi ad, hangi tip.
// Alias null ise SQL'e takma ad YAZILMAZ — tek veri setli sorgularda üretilen metin
// çok kaynaklılık eklenmeden önceki hâliyle birebir aynı kalsın diye.
public record ResolvedColumn(string? Alias, string Name, string Type);

// Sorgunun görebildiği veri setleri ve aralarındaki bağlar.
//
// Neden ayrı bir tip? Önceden şema düz bir sözlüktü (kolon → tip) ve tek veri seti
// varsayıyordu. Gerçek bir müşteride onlarca veri seti olur ve asıl değerli sorular
// ("satışları müşterinin şehrine göre grupla") birden fazlasına dokunur. Kolon çözümleme
// tek yerde toplanınca hem builder'lar sade kalıyor hem de belirsiz kolon adı sessiz
// yanlış sonuç yerine anlaşılır hata üretiyor.
public class QueryScope
{
    public IReadOnlyList<QuerySource> Sources { get; }
    public IReadOnlyList<QueryJoin> Joins { get; }

    // Tek kaynakta takma ad kullanılmaz (bkz. ResolvedColumn).
    public bool IsSingleSource => Sources.Count == 1;

    // Kolon kapsamda yoksa BAŞKA hangi veri setinde olduğunu söyleyen arayıcı (opsiyonel).
    // "Bilinmeyen kolon: sehir" teknik olarak doğru ama işe yaramaz bir mesaj; kullanıcının
    // öğrenmesi gereken şey kolonun nerede olduğudur.
    private readonly Func<string, string?>? _locate;

    public QueryScope(IReadOnlyList<QuerySource> sources, IReadOnlyList<QueryJoin>? joins = null,
        Func<string, string?>? columnLocator = null)
    {
        if (sources.Count == 0)
            throw new InvalidQueryException("Sorgu için en az bir veri seti gerekli.");

        Sources = sources;
        Joins = joins ?? Array.Empty<QueryJoin>();
        _locate = columnLocator;
    }

    // Tek veri setli (mevcut querystring uçlarının) kullanımı için kısayol.
    public static QueryScope Single(IReadOnlyDictionary<string, string> columns) =>
        new(new[] { new QuerySource("d0", Guid.Empty, "", columns) });

    public static QueryScope Single(Guid datasetId, string name,
        IReadOnlyDictionary<string, string> columns) =>
        new(new[] { new QuerySource("d0", datasetId, name, columns) });

    // "musteriler.sehir" ya da sade "sehir" biçimindeki referansı çözümler.
    //
    // Sade ad birden çok kaynakta geçiyorsa BELİRSİZDİR ve rastgele birini seçmek sessiz
    // yanlış cevap demektir — bu yüzden hata veriyoruz ve kullanıcıya nitelikli yazması
    // gerektiğini söylüyoruz.
    public ResolvedColumn Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new InvalidQueryException("Kolon adı boş olamaz.");

        var text = reference.Trim();
        var dot = text.IndexOf('.');

        if (dot > 0 && dot < text.Length - 1)
        {
            var sourceName = text[..dot];
            var columnName = text[(dot + 1)..];

            var source = Sources.FirstOrDefault(s =>
                             string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                         ?? Sources.FirstOrDefault(s =>
                             string.Equals(s.Alias, sourceName, StringComparison.OrdinalIgnoreCase));

            // Nokta içeren ad gerçek bir kolon adı da olabilir ("2026.ciro"): kaynak
            // bulunamazsa referansı bölmeden aramaya devam ediyoruz.
            if (source is not null)
            {
                if (!source.Columns.TryGetValue(columnName, out var type))
                    throw new InvalidQueryException(
                        $"'{source.Name}' veri setinde '{columnName}' diye bir kolon yok.");

                return new ResolvedColumn(AliasFor(source), columnName, type);
            }
        }

        var matches = Sources.Where(s => s.Columns.ContainsKey(text)).ToList();

        if (matches.Count == 1)
            return new ResolvedColumn(AliasFor(matches[0]), text, matches[0].Columns[text]);

        if (matches.Count > 1)
            throw new InvalidQueryException(
                $"'{text}' kolonu birden çok veri setinde var ({string.Join(", ", matches.Select(m => m.Name))}). " +
                "Hangisi olduğunu 'veriseti.kolon' biçiminde belirtin.");

        throw new InvalidQueryException(UnknownColumnMessage(text));
    }

    private string? AliasFor(QuerySource source) => IsSingleSource ? null : source.Alias;

    // Bilinmeyen kolon mesajı, mevcut kaynakları listeler. Çok kaynaklı sorgularda bu
    // mesaj kullanıcıya "hangi sette ne var" bilgisini de vermiş olur.
    private string UnknownColumnMessage(string column)
    {
        // Kolon başka bir veri setinde varsa bunu SÖYLE: kullanıcı böylece eksiğin kolon
        // değil, iki set arasındaki ilişki olduğunu anlar.
        var elsewhere = _locate?.Invoke(column);
        if (!string.IsNullOrWhiteSpace(elsewhere))
            return $"'{column}' kolonu bu sorguda kullanılan veri setlerinde yok; " +
                   $"'{elsewhere}' setinde bulunuyor. İki seti birleştirmek için aralarında " +
                   "ilişki tanımlanmalı.";

        var available = string.Join(" | ",
            Sources.Select(s => $"{s.Name}: {string.Join(", ", s.Columns.Keys)}"));

        return $"Bilinmeyen kolon: {column}. Kullanılabilir kolonlar → {available}";
    }
}
