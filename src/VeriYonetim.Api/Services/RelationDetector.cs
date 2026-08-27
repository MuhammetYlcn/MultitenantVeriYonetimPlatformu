using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

public interface IRelationDetector
{
    /// Yeni içeri alınan veri setini diğerleriyle karşılaştırır ve bulunan bağları kaydeder.
    /// Bulunan ilişki sayısını döner.
    Task<int> DetectAsync(Guid datasetId, CancellationToken ct = default);
}

// Veri setleri arasındaki bağı KENDİLİĞİNDEN bulur.
//
// Neden otomatik? "Satislar.musteri_no ile Musteriler.no aynı şeydir" bilgisi olmadan
// setler birleştirilemez. Bunu kullanıcıya sordurmak, ona sistemin iç işleyişini
// doldurtmak olurdu — kullanıcı dosyasını yükler, gerisini sistem çözmeli.
//
// Karar TAHMİNE değil ÖLÇÜME dayanır. Kolon adlarına hiç bakılmaz: "musteri_no" ile "no"
// birbirine hiç benzemez ama veri kendini ele verir —
//   1. Hedef kolon benzersiz mi? (her değer bir kez geçiyor mu → anahtar olabilir mi)
//   2. Kaynaktaki değerlerin kaçı hedefte var? (kapsama oranı)
// İkisi de sağlanıyorsa bu bir yabancı anahtardır.
public class RelationDetector : IRelationDetector
{
    // Eşik yüksek tutuluyor. Yanlış kurulmuş bir bağ, sessizce yanlış sayı üretir —
    // sistemin verebileceği en kötü cevap türü. Emin olamıyorsak bağ kurmuyoruz;
    // o zaman soru "bu iki set bağlı değil" der ve kullanıcı ekrandan elle tanımlar.
    private const double MinCoverage = 0.95;

    // Tek değerli kolonlar tesadüfen örtüşür (ör. her satırda aynı "TR"). Anlamlı bir
    // anahtar en az birkaç farklı değer taşır.
    private const int MinDistinctValues = 2;

    // Sınırlar: algılama içeri alma isteğini bekletmemeli.
    private const int MaxColumnsPerDataset = 12;
    private const int MaxOtherDatasets = 10;
    private const int MaxDistinctValues = 5000;

    // Önbelleğe yazarken ve okurken aynı seçenekler kullanılmalı.
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly AppDbContext _db;
    private readonly ILogger<RelationDetector> _logger;

    public RelationDetector(AppDbContext db, ILogger<RelationDetector> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Bir kolonun profili: benzersiz mi, hangi değerleri taşıyor.
    // Önbelleğe olduğu gibi serileştiriliyor (bkz. DatasetProfile).
    private record ColumnProfile(
        string Name,
        string Type,
        bool IsUnique,
        HashSet<string> Values);

    // Kurulmaya aday bir bağ: hangi setin hangi kolonu, hangi setin hangi kolonuna.
    // "From" yabancı anahtarı taşıyan, "To" anahtarın bulunduğu taraftır.
    private record Bag(
        Guid FromDatasetId, string FromColumn,
        Guid ToDatasetId, string ToColumn,
        double Coverage);

    public async Task<int> DetectAsync(Guid datasetId, CancellationToken ct = default)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == datasetId, ct);
        if (dataset is null) return 0;

        var others = await _db.Datasets
            .Where(d => d.Id != datasetId && d.RowCount > 0)
            .OrderByDescending(d => d.RowCount)
            .Take(MaxOtherDatasets)
            .ToListAsync(ct);

        if (others.Count == 0) return 0;

        // Zaten bağı olan set çiftlerine dokunma: kullanıcı elle tanımlamış ya da daha
        // önce bulunmuş olabilir. İkinci bir bağ eklemek gürültü yaratır.
        var linkedPairs = await _db.DatasetRelations
            .Select(r => new { r.FromDatasetId, r.ToDatasetId })
            .ToListAsync(ct);

        var linked = linkedPairs
            .SelectMany(p => new[] { (p.FromDatasetId, p.ToDatasetId), (p.ToDatasetId, p.FromDatasetId) })
            .ToHashSet();

        // Önbellek kayıtları tek sorguda: setler zaten elimizde, her biri için ayrı
        // gidip gelmenin anlamı yok.
        var ids = others.Select(o => o.Id).Append(datasetId).ToList();
        var cached = await _db.DatasetProfiles
            .Where(p => ids.Contains(p.DatasetId))
            .ToDictionaryAsync(p => p.DatasetId, ct);

        var sourceProfiles = await ProfileAsync(dataset, cached, ct);
        if (sourceProfiles.Count == 0) return 0;

        var found = 0;

        foreach (var other in others)
        {
            if (linked.Contains((datasetId, other.Id))) continue;

            var otherProfiles = await ProfileAsync(other, cached, ct);

            // İKİ YÖN de denenir. Anahtar yeni sette de olabilir: "Satışlar"ı önce,
            // "Müşteriler"i sonra yükleyen kullanıcı da bağı görmeli. Tek yön denenseydi
            // ilişkinin bulunup bulunmaması, dosyaların yüklenme SIRASINA bağlı olurdu.
            var match = BestMatch(sourceProfiles, otherProfiles) is { } forward
                ? new Bag(datasetId, forward.Source, other.Id, forward.Target, forward.Coverage)
                : null;

            if (BestMatch(otherProfiles, sourceProfiles) is { } backward &&
                (match is null || backward.Coverage > match.Coverage))
            {
                match = new Bag(other.Id, backward.Source, datasetId, backward.Target,
                    backward.Coverage);
            }

            if (match is null) continue;

            // "From" yabancı anahtarı taşıyan taraf, "To" anahtarın kendisi. Yönü
            // belirleyen şey hangi setin önce yüklendiği değil, hangi kolonun benzersiz
            // olduğu — yani verinin kendisi.
            _db.DatasetRelations.Add(new DatasetRelation
            {
                Id = Guid.NewGuid(),
                FromDatasetId = match.FromDatasetId,
                FromColumn = match.FromColumn,
                ToDatasetId = match.ToDatasetId,
                ToColumn = match.ToColumn,
                IsAutoDetected = true
            });

            var kaynakAdi = match.FromDatasetId == datasetId ? dataset.Name : other.Name;
            var hedefAdi = match.ToDatasetId == datasetId ? dataset.Name : other.Name;

            // Veri seti ve kolon ADLARI Debug'a indi: müşterinin şema bilgisi de müşteri
            // verisidir (sektörü, hangi alanları tuttuğu oradan okunur). Information'da
            // olayın ölçülebilir kısmı kalıyor — kaç ilişki, ne kapsamayla bulundu.
            _logger.LogInformation(
                "İlişki bulundu (kapsama %{Coverage:F0}).", match.Coverage * 100);
            _logger.LogDebug(
                "Bulunan ilişki: {From}.{FromCol} = {To}.{ToCol}",
                kaynakAdi, match.FromColumn, hedefAdi, match.ToColumn);

            found++;
        }

        // Koşulsuz: ilişki bulunmamış olsa bile bu koşuda hesaplanan profiller
        // önbelleğe yazılmalı, yoksa bir sonraki içe aktarma aynı işi tekrar yapar.
        await _db.SaveChangesAsync(ct);
        return found;
    }

    // En iyi kolon eşleşmesi: hedefi benzersiz olan ve kapsaması en yüksek çift.
    // Tek yönlüdür — çağıran iki yönü de dener (bkz. DetectAsync).
    private static (string Source, string Target, double Coverage)? BestMatch(
        IReadOnlyList<ColumnProfile> sources, IReadOnlyList<ColumnProfile> targets)
    {
        (string, string, double)? best = null;

        foreach (var source in sources)
        foreach (var target in targets)
        {
            // Tip uyuşmalı: metin kolonu sayısal bir anahtara bağlanmaz.
            if (source.Type != target.Type) continue;

            // Hedef benzersiz olmalı — yani anahtar olmaya elverişli olmalı. Aksi halde
            // birleştirme satırları ÇOĞALTIR ve toplamlar sessizce şişer.
            if (!target.IsUnique) continue;

            var coverage = Coverage(source.Values, target.Values);
            if (coverage < MinCoverage) continue;

            if (best is null || coverage > best.Value.Item3)
                best = (source.Name, target.Name, coverage);
        }

        return best;
    }

    private static double Coverage(HashSet<string> source, HashSet<string> target)
    {
        if (source.Count == 0) return 0;

        var matched = source.Count(target.Contains);

        return (double)matched / source.Count;
    }

    // Setin profili: önce önbellekten, yoksa ölçülerek.
    //
    // Buranın maliyeti kolon başına bir GROUP BY'dır ve setin BÜYÜKLÜĞÜYLE büyür —
    // 1 milyon satırlık bir komşu, 1.000 satırlık bir dosyanın içe aktarılmasını
    // saniyelerce bekletiyordu. Oysa o komşu değişmemişti.
    private async Task<List<ColumnProfile>> ProfileAsync(
        Dataset dataset, Dictionary<Guid, DatasetProfile> cached, CancellationToken ct)
    {
        var stamp = Stamp(dataset);

        if (cached.TryGetValue(dataset.Id, out var record) && record.Stamp == stamp)
        {
            var stored = Deserialize(record.Json);
            if (stored is not null) return stored;

            // Okunamayan önbellek görmezden gelinir; aşağıda yeniden ölçülüp üzerine yazılır.
            _logger.LogWarning("Profil önbelleği okunamadı: {Dataset}", dataset.Id);
        }

        var profiles = await MeasureAsync(dataset.Id, ct);

        Store(dataset.Id, stamp, profiles, record);

        return profiles;
    }

    // Setin damgası: satır yazan her uç UpdatedAt'i günceller (bkz. DatasetProfile.Stamp).
    private static DateTime Stamp(Dataset dataset) => dataset.UpdatedAt ?? dataset.CreatedAt;

    private void Store(
        Guid datasetId, DateTime stamp, List<ColumnProfile> profiles, DatasetProfile? existing)
    {
        var json = JsonSerializer.Serialize(profiles, JsonOptions);

        if (existing is null)
        {
            _db.DatasetProfiles.Add(new DatasetProfile
            {
                DatasetId = datasetId,
                Stamp = stamp,
                Json = json,
                ComputedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Stamp = stamp;
            existing.Json = json;
            existing.ComputedAt = DateTime.UtcNow;
        }
    }

    private static List<ColumnProfile>? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ColumnProfile>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Veri setinin kolonlarını ölçer: kolon başına tek sorgu, değer sayısı sınırlı.
    private async Task<List<ColumnProfile>> MeasureAsync(Guid datasetId, CancellationToken ct)
    {
        // Tarih kolonları anahtar olmaz; metin ve sayı yeterli.
        var columns = await _db.DatasetColumns
            .Where(c => c.DatasetId == datasetId && c.Type != "date")
            .OrderBy(c => c.Ordinal)
            .Take(MaxColumnsPerDataset)
            .Select(c => new { c.Name, c.Type })
            .ToListAsync(ct);

        var profiles = new List<ColumnProfile>(columns.Count);

        foreach (var column in columns)
        {
            var profile = await ProfileColumnAsync(datasetId, column.Name, column.Type, ct);
            if (profile is not null) profiles.Add(profile);
        }

        return profiles;
    }

    private async Task<ColumnProfile?> ProfileColumnAsync(
        Guid datasetId, string name, string type, CancellationToken ct)
    {
        // Değer + kaç kez geçtiği. Hepsi bir kez geçiyorsa kolon benzersizdir.
        // Kolon adı SQL'e gömüldüğü için tek tırnaklar ikiye katlanıyor.
        var sql = $"""
            SELECT ("Data"->>'{name.Replace("'", "''")}') AS v, COUNT(*)::int AS n
            FROM "DatasetRows"
            WHERE "DatasetId" = @datasetId AND ("Data"->>'{name.Replace("'", "''")}') IS NOT NULL
            GROUP BY 1
            LIMIT {MaxDistinctValues + 1}
            """;

        var values = new HashSet<string>();
        var unique = true;

        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("datasetId", datasetId));

        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(ct);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                values.Add(reader.GetString(0));
                if (reader.GetInt32(1) > 1) unique = false;
            }
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync();
        }

        // Sınıra dayandıysak profil güvenilmez (değerlerin tamamını görmedik) — atla.
        if (values.Count > MaxDistinctValues) return null;
        if (values.Count < MinDistinctValues) return null;

        return new ColumnProfile(name, type, unique, values);
    }
}
