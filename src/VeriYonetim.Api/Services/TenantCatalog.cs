namespace VeriYonetim.Api.Services;

// Tek bir veri seti: kimlik, ad, açıklama ve kolonları.
public record DatasetInfo(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyDictionary<string, string> Columns);

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
    public QueryScope BuildScope(IReadOnlyList<string> datasetNames)
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

        return new QueryScope(sources, BuildJoins(sources));
    }

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
