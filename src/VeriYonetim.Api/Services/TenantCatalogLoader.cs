using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Services;

/// <summary>
/// Firmanın veri kataloğunu (setler, kolonlar, aralarındaki ilişkiler) okur.
///
/// Ayrı bir servise çıkarıldı çünkü kataloğu artık İKİ yol kullanıyor: kullanıcının canlı
/// sorusu (AskController) ve izleyicinin arka plandaki koşusu (WatchRunner). İkisinin aynı
/// kataloğu görmesi zorunlu — izleyici, kurulduğu andakinden farklı bir katalogla çalışırsa
/// aynı planın anlamı değişirdi.
///
/// İzolasyonun dayanağı burada: sorgular global query filter'dan geçtiği için kataloğa
/// yalnız aktif firmanın setleri girer, kataloğa girmeyen bir set de sorguya giremez.
/// </summary>
public interface ITenantCatalogLoader
{
    Task<TenantCatalog> LoadAsync(CancellationToken ct = default);
}

public class TenantCatalogLoader : ITenantCatalogLoader
{
    private readonly AppDbContext _db;

    public TenantCatalogLoader(AppDbContext db) => _db = db;

    public async Task<TenantCatalog> LoadAsync(CancellationToken ct = default)
    {
        var datasets = await _db.Datasets
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.Description, d.RowCount })
            .ToListAsync(ct);

        var columns = await _db.DatasetColumns
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.DatasetId, c.Name, c.Type })
            .ToListAsync(ct);

        var byDataset = columns
            .GroupBy(c => c.DatasetId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)
                g.ToDictionary(c => c.Name, c => c.Type));

        var infos = datasets
            .Select(d => new DatasetInfo(d.Id, d.Name, d.Description,
                byDataset.GetValueOrDefault(d.Id, new Dictionary<string, string>()), d.RowCount))
            // Şemasız setler modele hiç gösterilmez: seçilirlerse sorgu kurulamaz ve
            // kullanıcı sebebini anlamayacağı bir hata alırdı.
            .Where(d => d.Columns.Count > 0)
            .ToList();

        var relations = await _db.DatasetRelations
            .Select(r => new RelationInfo(r.FromDatasetId, r.FromColumn, r.ToDatasetId, r.ToColumn))
            .ToListAsync(ct);

        return new TenantCatalog(infos, relations);
    }
}
