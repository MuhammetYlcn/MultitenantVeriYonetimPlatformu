using System.Globalization;
using System.Text;

namespace VeriYonetim.Olcum;

// Ölçüm çıktısını markdown tabloya çevirir.
//
// Tablo ölçekler SÜTUN olacak biçimde kurulur (senaryo × 10k/100k/1M): sorulan soru
// "bu sorgu ne kadar sürüyor" değil, "veri on kat büyüyünce ne oluyor". Sütunlar yan
// yana olmazsa o karşılaştırma okuyucunun kafasında yapılmak zorunda kalır.
internal static class Rapor
{
    private static readonly CultureInfo Tr = new("tr-TR");

    public static string Kur(IReadOnlyList<Sonuc> sonuclar, Dictionary<string, string> ortam,
        IReadOnlyList<YazmaSonuc> yazma)
    {
        var yazi = new StringBuilder();

        yazi.AppendLine($"# Ölçek ölçümü — {DateTime.Now:dd.MM.yyyy HH:mm}");
        yazi.AppendLine();

        foreach (var (anahtar, deger) in ortam)
            yazi.AppendLine($"- **{anahtar}:** {deger}");

        yazi.AppendLine();
        yazi.AppendLine("Süreler **medyan**, milisaniye. Isınma koşuları sayılmadı.");
        yazi.AppendLine();

        Bolum(yazi, sonuclar, "SQL",
            "## Veritabanı sorguları",
            "Sorguların SQL'i uygulamanın kendi builder'ları tarafından üretildi " +
            "(`DatasetRowQueryBuilder`, `DatasetAggregateQueryBuilder`) — elle yazılmadı.");

        Bolum(yazi, sonuclar, "uç",
            "## HTTP uçları",
            "Tarayıcının çağırdığı adresler, gerçek oturum token'ıyla. Satır listesi ucu " +
            "her çağrıda ayrıca `COUNT(*)` de koşturur (sayfa sayısı için).");

        Yazma(yazi, yazma);
        Ayrinti(yazi, sonuclar);

        return yazi.ToString();
    }

    // İçeri alma yolu. Okuma tablolarından ayrı duruyor çünkü ölçülen büyüklük süre değil,
    // saniyede yazılan satır.
    private static void Yazma(StringBuilder yazi, IReadOnlyList<YazmaSonuc> yazma)
    {
        if (yazma.Count == 0) return;

        yazi.AppendLine("## Veri yazma");
        yazi.AppendLine();
        yazi.AppendLine(
            "İki yol da BOŞ bir veri setine aynı sayıda satır yazıyor. Aynı işi yapmıyorlar: " +
            "uç CSV'yi ayrıştırıp şemaya göre doğruluyor, `COPY` hazır değer basıyor. " +
            "Karşılaştırmanın amacı da bu — doğrulamanın mı, yazmanın mı pahalı olduğu.");
        yazi.AppendLine();
        yazi.AppendLine("| yol | satır | süre (sn) | satır/sn | not |");
        yazi.AppendLine("|---|---:|---:|---:|---|");

        foreach (var y in yazma)
            yazi.AppendLine(
                $"| {y.Yol} | {y.Satir:N0} | {y.Saniye.ToString("N1", Tr)} | " +
                $"{y.SatirSn:N0} | {y.Not} |");

        yazi.AppendLine();
    }

    private static void Bolum(StringBuilder yazi, IReadOnlyList<Sonuc> sonuclar,
        string nokta, string baslik, string aciklama)
    {
        var kume = sonuclar.Where(s => s.Nokta == nokta).ToList();
        if (kume.Count == 0) return;

        var olcekler = kume.Select(s => s.Olcek).Distinct().ToList();

        yazi.AppendLine(baslik);
        yazi.AppendLine();
        yazi.AppendLine(aciklama);
        yazi.AppendLine();

        yazi.Append("| senaryo | ne ölçülüyor |");
        foreach (var olcek in olcekler) yazi.Append($" {olcek} |");
        yazi.AppendLine();

        yazi.Append("|---|---|");
        foreach (var _ in olcekler) yazi.Append("---:|");
        yazi.AppendLine();

        foreach (var ad in kume.Select(s => s.Ad).Distinct())
        {
            var satir = kume.Where(s => s.Ad == ad).ToList();
            yazi.Append($"| `{ad}` | {satir[0].Aciklama} |");

            foreach (var olcek in olcekler)
            {
                var hucre = satir.FirstOrDefault(s => s.Olcek == olcek);
                yazi.Append(hucre is null ? " — |" : $" {Ms(hucre.MedyanMs)} |");
            }

            yazi.AppendLine();
        }

        yazi.AppendLine();

        if (nokta == "SQL") Erisim(yazi, kume, olcekler);
    }

    // Süreden daha kalıcı olan bilgi: PostgreSQL tabloya NASIL erişti. Süre makineye
    // göre değişir, planın şekli değişmez — ve asıl bulgu burada görünür: aynı sorgu,
    // veri seti büyüdükçe indeksten vazgeçip tabloyu baştan taramaya geçiyor.
    private static void Erisim(StringBuilder yazi, List<Sonuc> kume, List<string> olcekler)
    {
        yazi.AppendLine("### Erişim biçimi (EXPLAIN)");
        yazi.AppendLine();

        yazi.Append("| senaryo |");
        foreach (var olcek in olcekler) yazi.Append($" {olcek} |");
        yazi.AppendLine();

        yazi.Append("|---|");
        foreach (var _ in olcekler) yazi.Append("---|");
        yazi.AppendLine();

        foreach (var ad in kume.Select(s => s.Ad).Distinct())
        {
            yazi.Append($"| `{ad}` |");

            foreach (var olcek in olcekler)
            {
                var hucre = kume.FirstOrDefault(s => s.Ad == ad && s.Olcek == olcek);
                yazi.Append($" {hucre?.Tarama ?? "—"} |");
            }

            yazi.AppendLine();
        }

        yazi.AppendLine();
    }

    // Medyanın gizlediği şey: en kötü koşu. Ayrı tabloda, dönen satır sayısıyla birlikte —
    // sayı sıfırsa ölçülen sorgu aslında hiçbir iş yapmamış demektir.
    private static void Ayrinti(StringBuilder yazi, IReadOnlyList<Sonuc> sonuclar)
    {
        yazi.AppendLine("## Ayrıntı");
        yazi.AppendLine();
        yazi.AppendLine("| senaryo | nokta | ölçek | medyan | en kötü | dönen satır |");
        yazi.AppendLine("|---|---|---|---:|---:|---:|");

        foreach (var s in sonuclar)
            yazi.AppendLine(
                $"| `{s.Ad}` | {s.Nokta} | {s.Olcek} | {Ms(s.MedyanMs)} | {Ms(s.EnKotuMs)} | {s.Satir:N0} |");

        yazi.AppendLine();
    }

    private static string Ms(double ms) => ms >= 100
        ? ms.ToString("N0", Tr)
        : ms.ToString("N1", Tr);

    // Koşarken ekranda görünen kısa hâli.
    public static void Yaz(Sonuc s) => Console.WriteLine(
        $"  {s.Olcek,-5} {s.Nokta,-3} {s.Ad,-22} {Ms(s.MedyanMs),8} ms  " +
        $"{s.Satir,7:N0} satır  {s.Tarama}");
}
