namespace VeriYonetim.Api.Services;

// Tek bir veri seti: kimlik, ad, açıklama ve kolonları.
public record DatasetInfo(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyDictionary<string, string> Columns,
    int RowCount = 0);

// İki veri seti arasındaki bağ (DatasetRelation'ın sorgu tarafındaki karşılığı).
public record RelationInfo(Guid FromDatasetId, string FromColumn, Guid ToDatasetId, string ToColumn);

// Firmanın bütün veri setleri ve aralarındaki ilişkiler — yani modele sunulan "dünya".
//
// Doğal dil sorgusu artık tek bir veri setine bağlı değil: kullanıcı "hangi sette arayayım"
// diye seçmez, model bütün kataloğu görüp hangi seti (ya da hangi setleri) kullanacağına
// kendisi karar verir. Gerçek bir müşteride onlarca set olur ve asıl değerli sorular
// birden fazlasına dokunur — "satışları müşterinin şehrine göre grupla" gibi.
public class TenantCatalog
{
    public IReadOnlyList<DatasetInfo> Datasets { get; }
    public IReadOnlyList<RelationInfo> Relations { get; }

    public TenantCatalog(IReadOnlyList<DatasetInfo> datasets, IReadOnlyList<RelationInfo> relations)
    {
        Datasets = datasets;
        Relations = relations;
    }

    public DatasetInfo? Find(string? name) => string.IsNullOrWhiteSpace(name)
        ? null
        : Datasets.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    // Seçilen veri setlerinden sorgu kapsamı kurar; aralarındaki bağları katalogdan bulur.
    //
    // requiredColumns verilirse, kapsamda bulunamayan kolonlar için gereken veri setleri
    // KENDİLİĞİNDEN eklenir — ilişkileri tanımlı olmak şartıyla.
    public QueryScope BuildScope(
        IReadOnlyList<string> datasetNames, IReadOnlyList<string>? requiredColumns = null)
    {
        if (datasetNames.Count == 0)
            throw new InvalidQueryException("Sorgunun hangi veri setini kullanacağı belirtilmemiş.");

        var sources = new List<QuerySource>(datasetNames.Count);
        var seen = new HashSet<Guid>();

        foreach (var name in datasetNames)
        {
            var dataset = Find(name)
                ?? throw new InvalidQueryException(UnknownDatasetMessage(name));

            // Aynı seti iki kez katmak (self-join) desteklenmiyor: takma ad çözümlemesi
            // belirsizleşir ve doğal dilde karşılığı olan bir soru da yok.
            if (!seen.Add(dataset.Id))
                throw new InvalidQueryException(
                    $"'{dataset.Name}' veri seti sorguya iki kez eklenemez.");

            if (dataset.Columns.Count == 0)
                throw new InvalidQueryException(
                    $"'{dataset.Name}' veri setinin şeması tanımlı değil; önce dosya yükleyin.");

            sources.Add(new QuerySource($"d{sources.Count}", dataset.Id, dataset.Name, dataset.Columns));
        }

        AddSourcesForMissingColumns(sources, seen, requiredColumns);

        return new QueryScope(sources, BuildJoins(sources), LocateColumn);
    }

    // Model gerekli veri setini join'e eklemeyi unutabilir: "şehirlere göre toplam satış"
    // sorusunda şehir Müşteriler'dedir ama model yalnız Satışlar'ı seçer.
    //
    // Bu durumda hata vermek yerine seti kendimiz ekliyoruz — çünkü belirsizlik YOK:
    // aranan kolonu içeren ve mevcut kaynaklardan birine TANIMLI ilişkiyle bağlanan tek
    // bir set varsa, kullanıcının kastettiği odur. Birden fazla aday varsa dokunmuyoruz;
    // orada tahmin yürütmek sessiz yanlış cevap üretirdi.
    private void AddSourcesForMissingColumns(
        List<QuerySource> sources, HashSet<Guid> seen, IReadOnlyList<string>? requiredColumns)
    {
        foreach (var reference in requiredColumns ?? Array.Empty<string>())
        {
            // Nitelikli referansın ("Musteriler.sehir") set adı zaten belli.
            var dot = reference.IndexOf('.');
            var wanted = dot > 0 ? reference[(dot + 1)..] : reference;
            var qualifier = dot > 0 ? reference[..dot] : null;

            if (sources.Any(s => s.Columns.ContainsKey(wanted) &&
                                 (qualifier is null ||
                                  string.Equals(s.Name, qualifier, StringComparison.OrdinalIgnoreCase))))
                continue;

            var candidates = Datasets
                .Where(d => !seen.Contains(d.Id))
                .Where(d => d.Columns.ContainsKey(wanted))
                .Where(d => qualifier is null ||
                            string.Equals(d.Name, qualifier, StringComparison.OrdinalIgnoreCase))
                .Where(d => IsRelatedToAny(d.Id, sources))
                .ToList();

            if (candidates.Count != 1) continue;

            var extra = candidates[0];
            seen.Add(extra.Id);
            sources.Add(new QuerySource($"d{sources.Count}", extra.Id, extra.Name, extra.Columns));
        }
    }

    private bool IsRelatedToAny(Guid datasetId, IReadOnlyList<QuerySource> sources) =>
        Relations.Any(r =>
            (r.FromDatasetId == datasetId && sources.Any(s => s.DatasetId == r.ToDatasetId)) ||
            (r.ToDatasetId == datasetId && sources.Any(s => s.DatasetId == r.FromDatasetId)));

    // Seçilen kaynaklar arasındaki ilişkileri takma adlara çevirir. Kataloğa tanımlanmamış
    // bir bağ varsa hata BuildFrom'da verilir (orada hangi setin bağlanamadığı bellidir).
    private List<QueryJoin> BuildJoins(IReadOnlyList<QuerySource> sources)
    {
        var aliasById = sources.ToDictionary(s => s.DatasetId, s => s.Alias);
        var joins = new List<QueryJoin>();

        foreach (var relation in Relations)
        {
            if (!aliasById.TryGetValue(relation.FromDatasetId, out var left)) continue;
            if (!aliasById.TryGetValue(relation.ToDatasetId, out var right)) continue;

            joins.Add(new QueryJoin(left, relation.FromColumn, right, relation.ToColumn));
        }

        return joins;
    }

    // Kolon başka bir veri setinde varsa bunu SÖYLE. "Bilinmeyen kolon" demek teknik olarak
    // doğru ama işe yaramaz; kullanıcının asıl öğrenmesi gereken şey kolonun nerede olduğu
    // ve iki seti bağlamak için ilişki tanımlaması gerektiğidir.
    public string? LocateColumn(string column)
    {
        var owners = Datasets
            .Where(d => d.Columns.ContainsKey(column))
            .Select(d => d.Name)
            .ToList();

        return owners.Count == 0 ? null : string.Join(", ", owners);
    }

    private string UnknownDatasetMessage(string name)
    {
        var available = string.Join(", ", Datasets.Select(d => d.Name));
        return Datasets.Count == 0
            ? "Bu firmada henüz veri seti yok."
            : $"'{name}' diye bir veri seti yok. Mevcut setler: {available}";
    }
}
