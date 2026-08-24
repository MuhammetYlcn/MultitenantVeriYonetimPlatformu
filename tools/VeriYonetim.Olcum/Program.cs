using Npgsql;
using VeriYonetim.Olcum;

// Ölçek ölçümü aracı.
//
// Hocanın 24.07 toplantısındaki sorusunun cevabı: "tek veritabanı, az tablo — büyüyünce
// veri bulmak zorlaşır." O gün cevap sözle verildi, ÖLÇÜLMEDİ. Bu araç ölçüyor.
//
//   seed      ölçüm veritabanını kurar ve veriyi basar (10k + 100k + 1M + gürültü firması)
//   measure   senaryoları koşturur ve raporu yazar
//   clean     ölçüm veritabanını siler
//
// `measure --indeks` verilirse ölçüm İKİ FAZ koşar: önce indeksler düşürülüp ölçülür,
// sonra aday ifade indeksleri kurulup aynı senaryolar tekrar ölçülür. İki koşu ayrı
// zamanlarda yapılıp sonradan karşılaştırılsaydı aradaki fark, indeksin etkisi kadar
// makinenin o anki hâlini de içerirdi.
//
// Örnek:
//   dotnet run --project tools/VeriYonetim.Olcum -- seed
//   dotnet run --project tools/VeriYonetim.Olcum -- measure --api http://localhost:5000
//   dotnet run --project tools/VeriYonetim.Olcum -- measure --api http://localhost:5000 --indeks
//   dotnet run --project tools/VeriYonetim.Olcum -- clean

var komut = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (komut)
{
    case "seed":
        await Tohumlama.KurAsync();
        break;

    case "measure":
        await OlcAsync(Secenek(args, "--api"), Secenek(args, "--out"),
            args.Contains("--indeks"));
        break;

    case "clean":
        await Tohumlama.TemizleAsync();
        break;

    default:
        Console.WriteLine(
            "Komutlar: seed | measure [--api <adres>] [--out <dosya>] [--indeks] | clean");
        break;
}

return;

static string? Secenek(string[] args, string ad)
{
    var i = Array.IndexOf(args, ad);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task OlcAsync(string? apiAdresi, string? cikti, bool indeksKarsilastir)
{
    await using var baglanti = new NpgsqlConnection(Ortam.BaglantiDizesi());
    await baglanti.OpenAsync();

    // Uç ölçümü için API'nin ÖLÇÜM veritabanına bakıyor olması gerekir; adres verilmezse
    // o bölüm atlanır ve rapor bunu söyler (sessizce eksik kalmasın).
    var istemci = apiAdresi is null ? null : await Olcucu.IstemciAsync(apiAdresi, Ortam.KullaniciEposta);

    IReadOnlyList<IndeksAdayi>? adaylar = null;
    Faz? indeksli = null;

    // Karşılaştırma isteniyorsa taban koşusu, indekslerin kesinlikle bulunmadığı bir
    // tablo üzerinde yapılmalı — önceki bir koşudan kalmış indeks, "öncesi" sütununu
    // sessizce iyileştirirdi.
    if (indeksKarsilastir)
    {
        Console.WriteLine("indeksler düşürülüyor (taban koşusu için)");
        await Indeksler.DusurAsync(baglanti);
    }

    var taban = await FazAsync(baglanti, istemci, apiAdresi, "indekssiz");

    // Ortam bilgisi taban fazının sonunda alınıyor: okumaların gördüğü tablo bu.
    var ortam = await OrtamBilgisiAsync(baglanti, apiAdresi);

    if (indeksKarsilastir)
    {
        Console.WriteLine("\nifade indeksleri kuruluyor");
        adaylar = await Indeksler.KurAsync(baglanti);

        indeksli = await FazAsync(baglanti, istemci, apiAdresi, "indeksli");
        ortam["İndeksli fazdaki indeksler"] = await IndeksListesiAsync(baglanti);
    }

    istemci?.Dispose();

    var metin = Rapor.Kur(taban, ortam, indeksli, adaylar);

    // Çıktı raporlar/ altına DEĞİL, depo kökündeki olcumler/ altına yazılıyor: raporlar/
    // staj günlüklerinin yeri ve .gitignore'da; ölçüm sonucu ise kodun kanıtı, depoda
    // durmalı (Pazartesi'nin "öncesi/sonrası" karşılaştırması buna dayanacak).
    var yol = cikti ?? Path.Combine(Ortam.DepoKoku(), "olcumler",
        $"olcum-{DateTime.Now:yyyyMMdd-HHmm}.md");

    Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
    await File.WriteAllTextAsync(yol, metin);

    Console.WriteLine($"\nRapor: {yol}");
}

// Bütün senaryoların bir kez baştan sona koşturulması. İndeks karşılaştırmasında bu iki
// kez çağrılır: bir kez indekssiz tabloda, bir kez ifade indeksleri kurulduktan sonra.
static async Task<Faz> FazAsync(
    NpgsqlConnection baglanti, HttpClient? istemci, string? apiAdresi, string etiket)
{
    var musteriSetId = await SetIdAsync(baglanti, Ortam.MusteriSeti);
    var sonuclar = new List<Sonuc>();

    foreach (var (setAdi, olcek, satirSayisi) in Ortam.Olcekler)
    {
        var setId = await SetIdAsync(baglanti, setAdi);

        Console.WriteLine($"\n[{etiket}] {olcek} ({satirSayisi:N0} satır) — {setId}");

        foreach (var senaryo in Senaryolar.Sql(setId, setAdi, satirSayisi, musteriSetId))
        {
            var sonuc = await Olcucu.SqlOlcAsync(baglanti, senaryo, olcek);
            sonuclar.Add(sonuc);
            Rapor.Yaz(sonuc);
        }

        if (istemci is null) continue;

        foreach (var senaryo in Senaryolar.Uc(setId, satirSayisi))
        {
            var sonuc = await Olcucu.UcOlcAsync(istemci, senaryo, olcek);
            sonuclar.Add(sonuc);
            Rapor.Yaz(sonuc);
        }
    }

    // Yazma yolu ancak uç üzerinden ölçülebilir: bugünkü içe aktarma bir HTTP ucudur.
    // İndeksli fazda da ölçülüyor, çünkü indeksin bedeli okumada değil YAZMADA çıkar.
    var yazma = new List<YazmaSonuc>();

    if (istemci is not null)
    {
        Console.WriteLine($"\n[{etiket}] yazma");
        yazma = await YazmaOlcumu.OlcAsync(baglanti, istemci, apiAdresi!);

        foreach (var y in yazma)
            Console.WriteLine($"  {y.Yol,-22} {y.Satir,7:N0} satır  {y.Saniye,6:N1} sn  " +
                              $"{y.SatirSn,8:N0} satır/sn  {y.Not}");
    }

    return new Faz(etiket, sonuclar, yazma);
}

static async Task<string> IndeksListesiAsync(NpgsqlConnection baglanti)
{
    var indeksler = new List<string>();

    await using var komut = baglanti.CreateCommand();
    komut.CommandText =
        "SELECT indexname FROM pg_indexes WHERE tablename = 'DatasetRows' ORDER BY indexname";

    await using var okuyucu = await komut.ExecuteReaderAsync();
    while (await okuyucu.ReadAsync()) indeksler.Add(okuyucu.GetString(0));

    return string.Join(", ", indeksler);
}

// Rapor makineden bağımsız okunamaz: aynı sorgu başka bir diskte başka süre verir.
// Bu yüzden ortam bilgisi raporun başına yazılıyor.
static async Task<Dictionary<string, string>> OrtamBilgisiAsync(
    NpgsqlConnection baglanti, string? apiAdresi)
{
    var bilgi = new Dictionary<string, string>
    {
        ["PostgreSQL"] = (await MetinAsync(baglanti, "SHOW server_version")) ?? "?",
        ["Tablodaki toplam satır"] =
            $"{await SayiAsync(baglanti, "SELECT COUNT(*) FROM \"DatasetRows\""):N0}",
        ["Veri setleri"] =
            $"{await SayiAsync(baglanti, "SELECT COUNT(*) FROM \"Datasets\"")} set / " +
            $"{await SayiAsync(baglanti, "SELECT COUNT(*) FROM \"Tenants\"")} firma",
        ["Tablo boyutu"] =
            (await MetinAsync(baglanti, "SELECT pg_size_pretty(pg_total_relation_size('\"DatasetRows\"'))")) ?? "?",
        ["İşlemci / çekirdek"] = $"{Environment.ProcessorCount} mantıksal çekirdek",
        ["Uç ölçümü"] = apiAdresi ?? "yapılmadı (--api verilmedi)"
    };

    // İndeks listesi Pazartesi'nin işinin çıkış noktası: bugünkü tablo yalnız birincil
    // anahtar ve DatasetId indeksini taşıyor.
    var indeksler = new List<string>();
    await using (var komut = baglanti.CreateCommand())
    {
        komut.CommandText =
            "SELECT indexname FROM pg_indexes WHERE tablename = 'DatasetRows' ORDER BY indexname";
        await using var okuyucu = await komut.ExecuteReaderAsync();
        while (await okuyucu.ReadAsync()) indeksler.Add(okuyucu.GetString(0));
    }

    bilgi["DatasetRows indeksleri"] = string.Join(", ", indeksler);

    return bilgi;
}

static async Task<Guid> SetIdAsync(NpgsqlConnection baglanti, string ad)
{
    await using var komut = baglanti.CreateCommand();
    komut.CommandText =
        """
        SELECT d."Id" FROM "Datasets" d
        JOIN "Tenants" t ON t."Id" = d."TenantId"
        WHERE t."Slug" = @slug AND d."Name" = @ad
        """;
    komut.Parameters.AddWithValue("slug", Ortam.OlculenSlug);
    komut.Parameters.AddWithValue("ad", ad);

    var deger = await komut.ExecuteScalarAsync();

    return deger is Guid id
        ? id
        : throw new InvalidOperationException($"'{ad}' veri seti yok. Önce `seed` çalıştırın.");
}

static async Task<long> SayiAsync(NpgsqlConnection baglanti, string sql)
{
    await using var komut = baglanti.CreateCommand();
    komut.CommandText = sql;
    return Convert.ToInt64(await komut.ExecuteScalarAsync());
}

static async Task<string?> MetinAsync(NpgsqlConnection baglanti, string sql)
{
    await using var komut = baglanti.CreateCommand();
    komut.CommandText = sql;
    return (await komut.ExecuteScalarAsync())?.ToString();
}
