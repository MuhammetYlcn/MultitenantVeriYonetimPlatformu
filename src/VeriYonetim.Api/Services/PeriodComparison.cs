namespace VeriYonetim.Api.Services;

// Bir grubun iki dönemdeki değeri ve aradaki fark.
// Previous/Delta null olabilir: o grup önceki dönemde HİÇ yoksa fark hesaplanamaz.
public record ComparisonBucket(
    string? Key,
    decimal? Current,
    decimal? Previous,
    decimal? Delta,
    decimal? DeltaPercent);

// "Bu ay geçen aya göre nasıl?" sorusunun cevabı tek bir SQL sorgusuyla üretilmez: aynı
// agregasyon iki farklı tarih aralığı için çalıştırılır, sonra sonuçlar burada eşleştirilir.
//
// Eşleştirme saf bir işlem olduğundan (DB/HTTP yok) ayrı duruyor ve doğrudan test edilebiliyor
// — asıl kırılganlık SQL'de değil, eksik grupların nasıl ele alındığında.
public static class PeriodComparison
{
    public static IReadOnlyList<ComparisonBucket> Merge(
        IReadOnlyList<(string? Key, decimal? Value)> current,
        IReadOnlyList<(string? Key, decimal? Value)> previous)
    {
        // Anahtar null olabilir (gruplamasız tek satır); sözlükte "" ile temsil edilir.
        var previousByKey = new Dictionary<string, decimal?>();
        foreach (var p in previous) previousByKey[p.Key ?? ""] = p.Value;

        var result = new List<ComparisonBucket>(current.Count);
        var seen = new HashSet<string>();

        foreach (var c in current)
        {
            var key = c.Key ?? "";
            seen.Add(key);

            var hasPrevious = previousByKey.TryGetValue(key, out var previousValue);
            result.Add(Make(c.Key, c.Value, hasPrevious ? previousValue : null));
        }

        // Yalnızca ÖNCEKİ dönemde bulunan gruplar da listeye girer. "Geçen ay Bursa'da satış
        // vardı, bu ay hiç yok" bilgisi en az artış kadar önemlidir; yalnızca current
        // üzerinden yürüseydik bu grup sessizce kaybolurdu.
        foreach (var p in previous)
        {
            var key = p.Key ?? "";
            if (!seen.Add(key)) continue;
            result.Add(Make(p.Key, null, p.Value));
        }

        return result;
    }

    private static ComparisonBucket Make(string? key, decimal? current, decimal? previous)
    {
        // Eksik dönem SIFIR SAYILMAZ. "Geçen ay bu şehirde kayıt yok" ile "geçen ay 0 sattık"
        // farklı şeylerdir; eksiği 0 varsayarsak uydurma bir artış yüzdesi üretmiş oluruz.
        var delta = current is { } c && previous is { } p ? c - p : (decimal?)null;

        // Yüzde değişim yalnızca önceki değer sıfırdan farklıysa tanımlıdır. Mutlak değerle
        // bölünür ki negatif tabanda işaret ters dönmesin.
        var deltaPercent = current is { } c2 && previous is { } p2 && p2 != 0m
            ? (c2 - p2) / Math.Abs(p2) * 100m
            : (decimal?)null;

        return new ComparisonBucket(key, current, previous, delta, deltaPercent);
    }
}
