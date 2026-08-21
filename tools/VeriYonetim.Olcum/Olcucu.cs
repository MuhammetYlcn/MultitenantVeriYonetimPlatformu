using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace VeriYonetim.Olcum;

// Tek bir senaryonun tek bir ölçekteki sonucu.
internal record Sonuc(
    string Ad, string Aciklama, string Nokta, string Olcek,
    double MedyanMs, double EnKotuMs, int Satir, string Tarama);

// Süre ölçümü. Üç kural:
//
// 1. ISINMA koşusu sayılmaz. İlk çağrı bağlantı kurulumunu, sorgu planlamasını ve soğuk
//    disk okumasını da içerir; kullanıcının gördüğü ikinci ve sonraki çağrılardır.
// 2. ORTALAMA değil MEDYAN raporlanır. Tek bir takılma ortalamayı sürükler; medyan
//    "tipik" süreyi söyler. En kötü koşu ayrıca yazılır, saklanmasın diye.
// 3. Uzun süren sorguda tekrar sayısı düşer. 20 saniyelik bir sorguyu dokuz kez koşmak
//    ölçümü saatlere yayardı, bilgiyi ise artırmazdı.
internal static class Olcucu
{
    private const int Isinma = 2;
    private const int NormalTekrar = 9;
    private const int YavasTekrar = 3;
    private const double YavasEsigiMs = 1000;

    public static async Task<Sonuc> SqlOlcAsync(
        NpgsqlConnection baglanti, SqlSenaryo senaryo, string olcek)
    {
        for (var i = 0; i < Isinma; i++) await SqlKosAsync(baglanti, senaryo);

        var ilk = await SqlKosAsync(baglanti, senaryo);
        var tekrar = ilk.Ms > YavasEsigiMs ? YavasTekrar : NormalTekrar;

        var sureler = new List<double> { ilk.Ms };
        for (var i = 1; i < tekrar; i++)
            sureler.Add((await SqlKosAsync(baglanti, senaryo)).Ms);

        var tarama = await TaramaAsync(baglanti, senaryo);

        return new Sonuc(senaryo.Ad, senaryo.Aciklama, "SQL", olcek,
            Medyan(sureler), sureler.Max(), ilk.Satir, tarama);
    }

    public static async Task<Sonuc> UcOlcAsync(HttpClient istemci, UcSenaryo senaryo, string olcek)
    {
        for (var i = 0; i < Isinma; i++) await UcKosAsync(istemci, senaryo);

        var ilk = await UcKosAsync(istemci, senaryo);
        var tekrar = ilk.Ms > YavasEsigiMs ? YavasTekrar : NormalTekrar;

        var sureler = new List<double> { ilk.Ms };
        for (var i = 1; i < tekrar; i++)
            sureler.Add((await UcKosAsync(istemci, senaryo)).Ms);

        return new Sonuc(senaryo.Ad, senaryo.Aciklama, "uç", olcek,
            Medyan(sureler), sureler.Max(), ilk.Satir, "—");
    }

    // Satırlar okunmadan ölçüm eksik olurdu: PostgreSQL sonucu akış hâlinde döndürür,
    // yani "sorgu bitti" demek "veri geldi" demek değil. Her hücreye dokunuluyor.
    private static async Task<(double Ms, int Satir)> SqlKosAsync(
        NpgsqlConnection baglanti, SqlSenaryo senaryo)
    {
        await using var komut = baglanti.CreateCommand();
        komut.CommandText = senaryo.Sql;
        komut.CommandTimeout = 600;
        foreach (var p in senaryo.Parametreler) komut.Parameters.Add(Kopyala(p));

        var saat = Stopwatch.StartNew();
        var satir = 0;

        await using (var okuyucu = await komut.ExecuteReaderAsync())
        {
            while (await okuyucu.ReadAsync())
            {
                for (var i = 0; i < okuyucu.FieldCount; i++)
                    if (!await okuyucu.IsDBNullAsync(i))
                        _ = okuyucu.GetValue(i);

                satir++;
            }
        }

        return (saat.Elapsed.TotalMilliseconds, satir);
    }

    private static async Task<(double Ms, int Satir)> UcKosAsync(HttpClient istemci, UcSenaryo senaryo)
    {
        var saat = Stopwatch.StartNew();

        using var yanit = await istemci.GetAsync(senaryo.Yol);
        var govde = await yanit.Content.ReadAsStringAsync();
        var ms = saat.Elapsed.TotalMilliseconds;

        if (!yanit.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"{senaryo.Ad}: uç {(int)yanit.StatusCode} döndü. {govde}");

        return (ms, SatirSay(govde));
    }

    // Yanıttaki satır/kova sayısı: ölçümün gerçekten veri getirdiğinin kanıtı.
    private static int SatirSay(string govde)
    {
        using var belge = JsonDocument.Parse(govde);
        var kok = belge.RootElement;

        foreach (var alan in new[] { "rows", "buckets", "items" })
            if (kok.TryGetProperty(alan, out var dizi) && dizi.ValueKind == JsonValueKind.Array)
                return dizi.GetArrayLength();

        return kok.ValueKind == JsonValueKind.Array ? kok.GetArrayLength() : 1;
    }

    // "DatasetRows" tablosuna nasıl erişildiği: indeksten mi, baştan sona tarayarak mı.
    // Süreden daha kalıcı bir bilgi — süre makineye göre değişir, planın şekli değişmez.
    private static async Task<string> TaramaAsync(NpgsqlConnection baglanti, SqlSenaryo senaryo)
    {
        await using var komut = baglanti.CreateCommand();
        komut.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + senaryo.Sql;
        komut.CommandTimeout = 600;
        foreach (var p in senaryo.Parametreler) komut.Parameters.Add(Kopyala(p));

        var metin = (await komut.ExecuteScalarAsync())?.ToString();
        if (string.IsNullOrWhiteSpace(metin)) return "—";

        using var belge = JsonDocument.Parse(metin);
        var dugumler = new List<string>();
        Gez(belge.RootElement[0].GetProperty("Plan"), dugumler);

        return dugumler.Count == 0 ? "—" : string.Join(" + ", dugumler.Distinct());
    }

    private static void Gez(JsonElement dugum, List<string> toplanan)
    {
        if (dugum.TryGetProperty("Relation Name", out var tablo) &&
            tablo.GetString() == "DatasetRows" &&
            dugum.TryGetProperty("Node Type", out var tip))
        {
            // "Bitmap Heap Scan" hangi indeksle beslendiğini söylemez; alt düğümdeki
            // "Bitmap Index Scan" da toplandığı için ikisi birlikte görünür.
            toplanan.Add(tip.GetString() ?? "?");
        }

        if (dugum.TryGetProperty("Plans", out var cocuklar))
            foreach (var cocuk in cocuklar.EnumerateArray())
                Gez(cocuk, toplanan);
    }

    // Bir NpgsqlParameter aynı anda iki komuta bağlanamaz; her koşu için tazesi üretilir
    // (DatasetsController'daki Params yardımcısıyla aynı gerekçe).
    private static NpgsqlParameter Kopyala(NpgsqlParameter kaynak) =>
        new(kaynak.ParameterName, kaynak.NpgsqlDbType) { Value = kaynak.Value };

    private static double Medyan(List<double> degerler)
    {
        var sirali = degerler.OrderBy(d => d).ToList();
        var orta = sirali.Count / 2;

        return sirali.Count % 2 == 1
            ? sirali[orta]
            : (sirali[orta - 1] + sirali[orta]) / 2;
    }

    // Ölçüm firmasının kullanıcısıyla giriş yapmış bir HTTP istemcisi. Uç ölçümü,
    // gerçek yetkilendirmeden geçen istekleri ölçer — token'sız bir kısayol değil.
    public static async Task<HttpClient?> IstemciAsync(string adres, string eposta)
    {
        var istemci = new HttpClient { BaseAddress = new Uri(adres), Timeout = TimeSpan.FromMinutes(10) };

        try
        {
            var yanit = await istemci.PostAsJsonAsync("/api/auth/login",
                new { email = eposta, password = Ortam.Sifre });

            if (!yanit.IsSuccessStatusCode)
            {
                Console.WriteLine($"Uç ölçümü atlandı: giriş {(int)yanit.StatusCode} döndü.");
                return null;
            }

            using var belge = JsonDocument.Parse(await yanit.Content.ReadAsStringAsync());
            var token = belge.RootElement.GetProperty("token").GetString();

            istemci.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return istemci;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Uç ölçümü atlandı: API'ye ulaşılamadı ({ex.Message}).");
            return null;
        }
    }
}
