using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VeriYonetim.Api.Services;

/// Saklanmaya hazır görüntü: baytlar ve içerik türü.
public record StoredImage(byte[] Bytes, string ContentType);

// Belge görüntüsüne yapılan hazırlıklar. İki ayrı amaç var ve ikisi de burada:
// modele gönderilecek hâl (başlık şeridi) ve saklanacak hâl (küçültme).
//
// Modele GÖNDERİLMEDEN ÖNCE yapılan müdahale:
//
// Buradaki tek iş, ölçümün en şaşırtıcı sonucunun karşılığı: bazı davranışlar istemle
// düzeltilemiyor, GİRDİYİ DEĞİŞTİREREK düzeltiliyor. Model, başlık satırı olmayan bir
// tabloda ilk veri satırını kolon adı sanıp harcıyordu (24 belgenin 11'inde bir satır
// eksik). İstem üç kez denendi ve tutmadı (v1 13/24, v2 5/24, v3 13/24); satır sayısını
// ayrıca sormak da işe yaramadı. Harcayacağı başlığı görüntüye kendimiz ekleyince veri
// satırına dokunmadı: 22/24, hücre doğruluğu %51 → %94.
//
// Ölçüm aracıyla (tools/belge_ureteci/olc.py, `baslik_seridi_ekle`) BİREBİR aynı geometriyi
// uyguluyor. Kasıtlı: ölçülen sayı ancak üretimde aynı şey yapılırsa geçerlidir. Punto,
// şerit yüksekliği ve adların eşit aralıkla dağıtılması oradan kopyalandı — "daha güzel"
// hizalama denemesi, ölçümü geçersiz kılardı.
public static class DocumentImagePrep
{
    // Yazı tipi sırayla aranır. Arial Windows'ta, DejaVu/Liberation ise Linux
    // konteynerlerinde bulunur — dockerize edilirken imaja bir yazı tipi paketi
    // (ör. fonts-dejavu-core) KURULMALI, yoksa şerit sessizce devre dışı kalır.
    private static readonly string[] FontCandidates =
    {
        "Arial", "DejaVu Sans", "Liberation Sans", "Segoe UI", "Noto Sans", "FreeSans"
    };

    /// Görüntünün üstüne kolon adlarından bir başlık şeridi ekler.
    ///
    /// Yazı tipi bulunamazsa false döner ve çıkarım şeritsiz devam eder: eksik bir yazı
    /// tipi yüzünden belge okumayı tümden reddetmek, çözdüğünden büyük bir sorun yaratır.
    public static bool TryAddHeaderBand(Image source, IReadOnlyList<string> columns,
        out Image? banded)
    {
        banded = null;
        if (columns.Count == 0) return false;
        if (!TryResolveFontFamily(out var family)) return false;

        var width = source.Width;
        var fontSize = Math.Max(14, width / 38f);
        var bandHeight = (int)(fontSize * 2.4f);
        var font = family.CreateFont(fontSize, FontStyle.Bold);

        var canvas = new Image<Rgb24>(width, source.Height + bandHeight, Color.White);

        // Kolonların belgedeki GERÇEK konumu bilinmiyor (şema ad listesi, koordinat değil).
        // Bu yüzden adlar genişliğe eşit aralıklarla dağıtılıyor. Hiza kaba ama ölçüm
        // gösterdi ki model kolonları buna rağmen karıştırmıyor: şeridin işlevi konum
        // bildirmek değil, "başlık zaten var" demek.
        var slice = width / (float)columns.Count;

        canvas.Mutate(ctx =>
        {
            ctx.DrawImage(source, new Point(0, bandHeight), 1f);

            for (var i = 0; i < columns.Count; i++)
                ctx.DrawText(columns[i], font, Color.FromRgb(20, 20, 20),
                    new PointF(i * slice + fontSize / 2f, fontSize / 2f));

            // Şeridi belgeden ayıran ince çizgi: modelin şeridi belgenin bir parçası
            // sanmasını değil, başlık satırı olarak görmesini istiyoruz.
            ctx.DrawLine(Color.FromRgb(110, 110, 110), 2f,
                new PointF(0, bandHeight - 3), new PointF(width, bandHeight - 3));
        });

        banded = canvas;
        return true;
    }

    /// <summary>
    /// Görüntüyü SAKLANACAK hâline getirir: gösterime yetecek boya indirir ve JPEG'e çevirir.
    ///
    /// Neden saklıyoruz: asenkron akışta kullanıcı işi başlatıp ekrandan çıkabiliyor; geri
    /// döndüğünde onay ekranı belgeyi hücrelerin yanında göstermek zorunda, ama istemcinin
    /// elindeki dosya çoktan gitmiş olur.
    ///
    /// Neden küçülterek: yüklenen dosya 15 MB'a kadar olabiliyor, oysa ekranda gösterilecek
    /// bir görüntü için bu boy gereksiz. Uzun kenar sınırı okunabilirlik gözetilerek
    /// seçildi — fatura kalem satırları hâlâ okunuyor, dosya ise birkaç yüz kilobayta
    /// iniyor. MODELE giden görüntü bundan bağımsız hazırlanıyor (bkz. DocumentVisionService):
    /// oradaki boy bağlam bütçesine göre hesaplanıyor, buradaki insan gözüne göre.
    /// </summary>
    public static async Task<StoredImage> PrepareForStorageAsync(byte[] original,
        string originalContentType, int maxLongEdge, CancellationToken ct = default)
    {
        using var input = new MemoryStream(original);
        using var image = await Image.LoadAsync(input, ct);

        // Büyütme yapılmaz: küçük bir fotoğrafı esnetmek dosyayı büyütür, okunabilirliği artırmaz.
        if (Math.Max(image.Width, image.Height) > maxLongEdge)
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(maxLongEdge, maxLongEdge),
                Mode = ResizeMode.Max,          // en-boy oranını korur
                Sampler = KnownResamplers.Lanczos3
            }));

        using var buffer = new MemoryStream();
        await image.SaveAsJpegAsync(buffer, new JpegEncoder { Quality = 85 }, ct);
        var encoded = buffer.ToArray();

        // Yeniden kodlama HER ZAMAN kazandırmaz ve bu ölçülerek görüldü: sınırın altındaki
        // bir fotoğrafta (66 KB) çıktı 87 KB'a çıktı. Sebebi açık — kaynak zaten sıkıştırılmış
        // bir JPEG'se, onu çözüp yeniden kodlamak hem kalite kaybettirir hem dosyayı
        // büyütebilir. Bu yüzden sonuç orijinalden küçük DEĞİLSE orijinal saklanıyor.
        //
        // İçerik türü istemcinin gönderdiği başlıktan değil, doğrulanmış uzantıdan
        // türetiliyor: başlığı gönderen taraf istemci, uzantıyı ise sunucu denetliyor.
        return encoded.Length < original.Length
            ? new StoredImage(encoded, "image/jpeg")
            : new StoredImage(original, originalContentType);
    }

    /// Doğrulanmış dosya uzantısından içerik türü.
    public static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    private static bool TryResolveFontFamily(out FontFamily family)
    {
        foreach (var name in FontCandidates)
            if (SystemFonts.TryGet(name, out family))
                return true;

        // Ada göre bulunamadıysa kurulu HERHANGİ bir yazı tipi iş görür: şeridin okunması
        // yeterli, tipografisi önemli değil.
        var any = SystemFonts.Families.FirstOrDefault();
        if (any != default)
        {
            family = any;
            return true;
        }

        family = default;
        return false;
    }
}
