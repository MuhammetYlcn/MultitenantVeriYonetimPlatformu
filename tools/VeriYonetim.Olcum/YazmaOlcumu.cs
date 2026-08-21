using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace VeriYonetim.Olcum;

// Tek bir yazma yolunun ölçümü.
internal record YazmaSonuc(string Yol, int Satir, double Saniye, double SatirSn, string Not);

// Veriyi İÇERİ ALMA yolunun ölçümü — okuma yollarının aksine bu, bugünkü uygulamanın
// bilinen bir zayıf noktası: `POST /datasets/{id}/rows` satırları EF üzerinden ekliyor
// (DatasetsController'da not düşülü). Ne kadar zayıf olduğu bugüne kadar ölçülmedi;
// Pazartesi'nin işi (toplu yazımın üretim yoluna taşınması) bu sayıya dayanacak.
//
// İki yol aynı koşulda ölçülür: her ikisi de BOŞ bir veri setine N satır yazar. Uçtaki
// "önce eskileri sil" maliyeti bilinçli olarak dışarıda — karşılaştırılan şey ekleme.
internal static class YazmaOlcumu
{
    // Uç ölçeklerinin üst sınırını dosya boyutu belirliyor: içe aktarma ucu 10 MB'ı
    // aşan dosyayı reddediyor. 50.000 satırlık CSV ~4,5 MB; 100.000 satır sınırı aşar.
    // Bu da bir bulgu: bugünkü yol tek dosyada 100.000 satırı zaten kabul etmiyor.
    private static readonly int[] Olcekler = { 1_000, 10_000, 50_000 };

    public static async Task<List<YazmaSonuc>> OlcAsync(
        NpgsqlConnection baglanti, HttpClient istemci, string apiAdresi)
    {
        var sonuclar = new List<YazmaSonuc>();

        var ucSeti = await ScratchSetAsync(baglanti, Ortam.OlculenSlug, "yazma_uc");
        var copySeti = await ScratchSetAsync(baglanti, Ortam.OlculenSlug, "yazma_copy");

        // Aynı içe aktarma, KOMŞUSUZ bir firmada. Aradaki fark ilişki algılamasının
        // bedelidir: algılama, yüklenen dosyayı firmanın DİĞER setleriyle karşılaştırır
        // ve bunu her içe aktarmada baştan yapar (bkz. RelationDetector.DetectAsync,
        // komşu yoksa erken döner). İki ölçüm olmadan bu maliyet, yazmanın kendi
        // maliyetiymiş gibi görünürdü.
        var yalnizSeti = await ScratchSetAsync(baglanti, Ortam.YalnizSlug, "yazma_yalniz");
        var yalnizIstemci = await Olcucu.IstemciAsync(apiAdresi, Ortam.YalnizEposta);

        foreach (var n in Olcekler)
        {
            var csv = CsvUret(n);

            await BosaltAsync(baglanti, ucSeti);
            sonuclar.Add(await UcOlcAsync(istemci, ucSeti, n, csv, "uç — 4 komşu set"));

            if (yalnizIstemci is not null)
            {
                await BosaltAsync(baglanti, yalnizSeti);
                sonuclar.Add(await UcOlcAsync(
                    yalnizIstemci, yalnizSeti, n, csv, "uç — komşusuz firma"));
            }

            await BosaltAsync(baglanti, copySeti);
            sonuclar.Add(await CopyOlcAsync(baglanti, copySeti, n));
        }

        yalnizIstemci?.Dispose();
        await BosaltAsync(baglanti, yalnizSeti);

        // Ölçüm kendi çöpünü bırakmıyor: tablo, tohumlama sonrası hâline dönüyor ki
        // ikinci bir `measure` koşusu birincisiyle aynı tabloyu ölçsün.
        await BosaltAsync(baglanti, ucSeti);
        await BosaltAsync(baglanti, copySeti);

        return sonuclar;
    }

    private static async Task<YazmaSonuc> UcOlcAsync(
        HttpClient istemci, Guid setId, int n, string csv, string yol)
    {
        var bayt = Encoding.UTF8.GetBytes(csv);

        using var icerik = new MultipartFormDataContent();
        var dosya = new ByteArrayContent(bayt);
        dosya.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        icerik.Add(dosya, "file", "olcum.csv");

        var saat = Stopwatch.StartNew();
        using var yanit = await istemci.PostAsync($"/api/datasets/{setId}/rows", icerik);
        var govde = await yanit.Content.ReadAsStringAsync();
        var saniye = saat.Elapsed.TotalSeconds;

        if (!yanit.IsSuccessStatusCode)
            return new YazmaSonuc(yol, n, saniye, 0,
                $"HATA {(int)yanit.StatusCode}: {Kisalt(govde)}");

        // Kaç satırın gerçekten yazıldığı yanıtın içinde: ölçüm "istek döndü" değil
        // "veri girdi" demek olmalı.
        using var belge = JsonDocument.Parse(govde);
        var yazilan = belge.RootElement.GetProperty("imported").GetInt32();
        var basarisiz = belge.RootElement.GetProperty("failed").GetInt32();

        var not = basarisiz == 0
            ? $"{bayt.Length / 1024.0 / 1024.0:N1} MB CSV"
            : $"{basarisiz} satır elendi";

        return new YazmaSonuc(yol, yazilan, saniye,
            yazilan / Math.Max(saniye, 0.001), not);
    }

    private static async Task<YazmaSonuc> CopyOlcAsync(NpgsqlConnection baglanti, Guid setId, int n)
    {
        var uretici = new Uretici(n);
        var secenekler = new JsonSerializerOptions();

        var saat = Stopwatch.StartNew();

        await using (var yazici = await baglanti.BeginBinaryImportAsync(
            "COPY \"DatasetRows\" (\"Id\", \"Data\", \"DatasetId\") FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < n; i++)
            {
                await yazici.StartRowAsync();
                await yazici.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
                await yazici.WriteAsync(
                    JsonSerializer.Serialize(uretici.Satis(i), secenekler), NpgsqlDbType.Jsonb);
                await yazici.WriteAsync(setId, NpgsqlDbType.Uuid);
            }

            await yazici.CompleteAsync();
        }

        var saniye = saat.Elapsed.TotalSeconds;

        return new YazmaSonuc("COPY (ikili akış)", n, saniye, n / Math.Max(saniye, 0.001),
            "doğrulama ve CSV ayrıştırma yok");
    }

    // Uç, CSV'yi ayrıştırır ve şemaya göre DOĞRULAR; COPY ise hazır değer yazar. Bu yüzden
    // iki sayı birebir aynı işi ölçmez — fark rapora yazılıyor (bkz. Not sütunu).
    private static string CsvUret(int n)
    {
        var uretici = new Uretici(n);
        var yazi = new StringBuilder();

        yazi.AppendLine(string.Join(",", Ortam.SatisSemasi.Select(k => k.Ad)));

        for (var i = 0; i < n; i++)
        {
            var satir = uretici.Satis(i);
            yazi.AppendLine(string.Join(",", Ortam.SatisSemasi.Select(k => Hucre(satir[k.Ad]))));
        }

        return yazi.ToString();
    }

    private static string Hucre(object? deger) => deger switch
    {
        null => "",
        DateTime t => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        _ => $"\"{deger.ToString()!.Replace("\"", "\"\"")}\""
    };

    // Yazma ölçümünün boş setleri `seed` tarafından kurulur; burada yalnız bulunur.
    private static async Task<Guid> ScratchSetAsync(
        NpgsqlConnection baglanti, string slug, string ad)
    {
        await using var bul = baglanti.CreateCommand();
        bul.CommandText =
            """
            SELECT d."Id" FROM "Datasets" d
            JOIN "Tenants" t ON t."Id" = d."TenantId"
            WHERE t."Slug" = @slug AND d."Name" = @ad
            """;
        bul.Parameters.AddWithValue("slug", slug);
        bul.Parameters.AddWithValue("ad", ad);

        return await bul.ExecuteScalarAsync() is Guid setId
            ? setId
            : throw new InvalidOperationException($"'{ad}' seti yok. Önce `seed` çalıştırın.");
    }

    // Satırlar silinirken RowCount da sıfırlanıyor: ilişki algılaması komşu setleri
    // RowCount > 0 diye seçiyor, boş bırakılan bir sayaç bir sonraki koşuda sahte komşu
    // üretirdi (bkz. RelationDetector).
    private static async Task BosaltAsync(NpgsqlConnection baglanti, Guid setId)
    {
        await using var komut = baglanti.CreateCommand();
        komut.CommandText =
            """
            DELETE FROM "DatasetRows" WHERE "DatasetId" = @set;
            UPDATE "Datasets" SET "RowCount" = 0 WHERE "Id" = @set;
            """;
        komut.CommandTimeout = 600;
        komut.Parameters.AddWithValue("set", setId);
        await komut.ExecuteNonQueryAsync();
    }

    private static string Kisalt(string metin) =>
        metin.Length <= 200 ? metin : metin[..200] + "…";
}
