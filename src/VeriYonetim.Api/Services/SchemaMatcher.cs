using System.Text;

namespace VeriYonetim.Api.Services;

// Eşleme için gereken asgari set bilgisi (kolonları + kimliği).
public record DatasetSchema(Guid DatasetId, string Name, IReadOnlyList<ColumnSchema> Columns);

// Belgede bulunan bir kolonun hedef setteki karşılığı.
//
// TypeConflict: adlar tutuyor ama tipler tutmuyor (ör. belgede "tutar" metin okundu, sette
// sayı). Eşleme yine de kuruluyor — ama işaretli, çünkü onay ekranında düzeltilecek olan
// budur ve sessizce atılırsa kullanıcı kolonun neden kaybolduğunu anlamaz.
public record ColumnMapping(string Discovered, string Target, bool TypeConflict);

public record DatasetMatch(
    Guid DatasetId,
    string Name,
    double Score,
    IReadOnlyList<ColumnMapping> Mappings,
    IReadOnlyList<string> MissingColumns,   // hedefte var, belgede çıkmadı
    IReadOnlyList<string> ExtraColumns);    // belgede çıktı, hedefte yok

// Keşif geçişinde bulunan kolonları, firmanın VAR OLAN veri setlerinin şemalarıyla
// karşılaştırır ve "bu belge şu sete ait olabilir" önerisi üretir.
//
// Neden gerekli: keşif geçişi şemasız çalışır, yani model kolon adlarını kendisi seçer.
// Öneri olmazsa kullanıcı her belgede yeni bir set açar ve aynı fatura üç ayrı sete
// dağılır — pano ve doğal dilde sorgu o veriyi bir daha bir arada göremez.
//
// Karar TAHMİN, sonuç ÖNERİ. RelationDetector'daki ilkenin aynısı geçerli: emin
// değilsek eşleme kurmuyoruz, "yeni set" diyoruz. Yanlış sete yazılmış bir belge
// sessizce yanlış toplam üretir; yeni set açmanın bedeli ise yalnız bir fazladan kart.
public static class SchemaMatcher
{
    // Bu eşiğin altındaki aday hiç önerilmez. Dice katsayısı 0,6, kabaca "iki taraftaki
    // kolonların üçte ikisi örtüşüyor" demek.
    public const double MinScore = 0.6;

    // Tek kolonun tutması yetmez: neredeyse her ticari belgede "tarih" vardır, o tek
    // başına belgeyi bir sete bağlamaz.
    private const int MinMatchedColumns = 2;

    // Kaç aday döndürülür. Kullanıcı bir listeden seçecek, sıralı okumayacak.
    private const int MaxCandidates = 3;

    /// Belgede bulunan kolonları adaylarla karşılaştırır; eşiği geçen adayları
    /// puanı yüksekten düşüğe döndürür. Hiçbiri geçmezse boş liste — bu "yeni set aç"
    /// demektir, en yakın kötü adayı önermek değil.
    public static IReadOnlyList<DatasetMatch> Match(
        IReadOnlyList<ColumnSchema> discovered, IEnumerable<DatasetSchema> candidates)
    {
        if (discovered.Count == 0) return Array.Empty<DatasetMatch>();

        return candidates
            .Select(c => Score(discovered, c))
            .Where(m => m.Score >= MinScore && m.Mappings.Count >= MinMatchedColumns)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Name)                 // eşit puanda sıra kararlı olsun
            .Take(MaxCandidates)
            .ToList();
    }

    /// Tek bir adayın puanı ve eşleme ayrıntısı. Test edilebilsin diye ayrı.
    public static DatasetMatch Score(
        IReadOnlyList<ColumnSchema> discovered, DatasetSchema candidate)
    {
        var mappings = new List<ColumnMapping>();
        var matchedTargets = new HashSet<string>(StringComparer.Ordinal);
        var extras = new List<string>();
        var total = 0.0;

        // Açgözlü eşleme: her belge kolonu için hedefte kalan en iyi karşılık aranır.
        // Kolon sayıları küçük olduğu için (onlarca, binlerce değil) en iyi çiftlemeyi
        // aramaya gerek yok; açgözlü seçim aynı sonucu veriyor ve okunabiliyor.
        foreach (var column in discovered)
        {
            var best = candidate.Columns
                .Where(t => !matchedTargets.Contains(t.Name))
                .Select(t => (Target: t, Weight: NameScore(column.Name, t.Name)))
                .OrderByDescending(x => x.Weight)
                .FirstOrDefault();

            if (best.Target is null || best.Weight <= 0)
            {
                extras.Add(column.Name);
                continue;
            }

            // Tip uyuşmazlığı eşlemeyi bozmaz ama puanı yarıya indirir: aynı adı taşıyan
            // ama biri tarih biri metin olan iki kolon "aynı kolon" sayılamaz.
            var conflict = !TypesCompatible(column.Type, best.Target.Type);
            matchedTargets.Add(best.Target.Name);
            mappings.Add(new ColumnMapping(column.Name, best.Target.Name, conflict));
            total += conflict ? best.Weight * 0.5 : best.Weight;
        }

        var missing = candidate.Columns
            .Where(t => !matchedTargets.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();

        // Dice katsayısı: 2 * eşleşen / (hedef + belge). Simetrik olması bilinçli —
        // hem hedefin eksik kalan kolonlarını hem belgenin fazla kolonlarını cezalandırır.
        // Tek yönlü kapsama ölçseydik, 20 kolonluk bir set 3 kolonluk her belgeyi
        // "tam eşleşme" gibi gösterirdi.
        var score = 2 * total / (candidate.Columns.Count + discovered.Count);

        return new DatasetMatch(candidate.DatasetId, candidate.Name, Math.Round(score, 3),
            mappings, missing, extras);
    }

    // İki kolon adının ne kadar aynı şeyi anlattığı: 1 (aynı), 0,7 (biri diğerini
    // kapsıyor), 0 (alakasız). Ara değer YOK — "biraz benziyor" bir eşleme gerekçesi
    // değil, ve puanı sürekli yapmak eşiği anlamsızlaştırır.
    public static double NameScore(string left, string right)
    {
        var a = Tokens(left);
        var b = Tokens(right);
        if (a.Count == 0 || b.Count == 0) return 0;

        if (a.SetEquals(b)) return 1.0;

        // "tarih" ⊂ "fatura_tarihi": belgede kısa, sette nitelenmiş ad (ya da tersi).
        if (a.IsSubsetOf(b) || b.IsSubsetOf(a)) return 0.7;

        return 0;
    }

    // Metin her şeyi kabul eder; bir belgeden okunan değer tipsiz gelir ve hedef metinse
    // sorun yok. Sayı ile tarih ise birbirinin yerine geçmez.
    private static bool TypesCompatible(string discovered, string target) =>
        discovered == target || target == "text";

    /// Kolon adını karşılaştırılabilir parçalara ayırır.
    ///
    /// Üç iş yapılıyor ve üçü de Türkçe kolon adlarının gözlenen hâlinden çıktı:
    ///   1. Türkçe harfler sadeleştirilir  ("Ürün Adı" → "urun adi")
    ///   2. ayraçlar boşluğa çevrilir      ("fatura_no" ≡ "fatura no" ≡ "faturaNo" DEĞİL)
    ///   3. iyelik eki atılır              ("tutarı" → "tutar", "numarası" → "numara")
    ///
    /// Üçüncüsü olmadan eşleme Türkçede neredeyse hiç çalışmıyor: CSV başlıkları "tutar"
    /// yazarken model belgeden "tutarı" okuyor ve iki kolon farklı sayılıyordu.
    public static HashSet<string> Tokens(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            sb.Append(Fold(char.ToLowerInvariant(ch)));

        return sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripSuffix)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Fold(char ch) => ch switch
    {
        'ı' or 'î' => "i",
        'ş' => "s",
        'ğ' => "g",
        'ü' or 'û' => "u",
        'ö' => "o",
        'ç' => "c",
        'â' => "a",
        _ when char.IsLetterOrDigit(ch) => ch.ToString(),
        // Harf ve rakam dışındaki her şey ayraç sayılır: alt çizgi, tire, boşluk, parantez.
        _ => " ",
    };

    // Türkçe iyelik ekinin kaba atılması. Sözlük yok, kural iki satır: sonda "si/sı/su/sü"
    // ya da tek ünlü. Kısa kelimeler korunuyor ("ad", "no", "kdv" bozulmasın diye).
    private static string StripSuffix(string token)
    {
        if (token.Length > 4 && token.EndsWith("si", StringComparison.Ordinal))
            return token[..^2];

        if (token.Length > 3 && (token[^1] == 'i' || token[^1] == 'u'))
            return token[..^1];

        return token;
    }
}
