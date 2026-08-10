using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeriYonetim.Api.Services;

// Görsel modelin bir belgeden çıkardığı içerik.
//
// Değerler METİN olarak tutuluyor, tipli değil: tip kararı kolonun tamamına bakan
// ValueFormats'ın işi (bkz. DatasetImportService). Burada hücreyi tiplemeye kalkmak o kararı
// ikiye böler ve iki yer ayrışırsa veri sessizce kaybolur.
//
// Notlar kullanıcıya gösterilmek üzere: çıkarımda yapılan her müdahale (düşürülen satır,
// uyuşmayan toplam) burada birikiyor. Onay ekranı bunları gösterir — sessiz düzeltme yok.
public record ExtractedDocument(
    IReadOnlyDictionary<string, string?> Fields,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Items,
    IReadOnlyList<string> Notes);

// Görsel modelin ham metnini ExtractedDocument'a çeviren katman.
//
// Kasıtlı olarak HTTP'den ve veritabanından bağımsız: buradaki kuralların tamamı gözlenmiş
// model davranışına karşı yazıldı ve testleri o gözlemlerden kuruldu (bkz.
// DocumentExtractionTests). Model değiştiğinde değişmesi gereken yer burası.
public static class DocumentExtractionParser
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Kod bloğu işaretleri: modele "yalnız JSON döndür" denmesine rağmen yanıtı ```json ile
    // sarmalıyor. Bunu hata saymak yerine soyuyoruz; ölçülen şey biçim değil içerik.
    private static readonly Regex Fence = new(@"^```(?:json)?|```$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Binlik ayıraçlı sayı: ayraçtan sonra TAM üç hane. ValueFormats.ThousandsDot ile aynı
    // kural, farkı burada virgül de kabul ediliyor — model iki ayracı karışık kullanıyor.
    private static readonly Regex Thousands = new(@"^-?\d{1,3}([.,]\d{3})+$", RegexOptions.Compiled);

    // Tek ayraçlı ondalık: "721,83" / "1993.32" / "-0,5"
    private static readonly Regex Decimal1 = new(@"^-?\d+[.,]\d+$", RegexOptions.Compiled);

    private static readonly Regex Integer = new(@"^-?\d+$", RegexOptions.Compiled);

    // Sayının başına/sonuna takılan süsler. "1.500,75 TL", "%20", "* 45,00" gözlendi.
    private static readonly Regex Susler = new(@"^[*\s%₺]+|[\s%₺]+$|\s*(TL|TRY)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Ham model metnini çözer. Çözülemezse null döner — çağıran bunu kullanıcıya
    /// "belge okunamadı" olarak bildirir, boş sonuç uydurmaz.
    public static ExtractedDocument? Parse(string modelText)
    {
        var kok = KokNesne(modelText);
        if (kok is null) return null;

        var notlar = new List<string>();
        var (alanlarDugum, duzYapi) = AlanlarDugumu(kok.Value);
        if (duzYapi)
            notlar.Add("Model beklenen \"alanlar\" sarmalayıcısını atladı; alanlar en üst düzeyden okundu.");

        var alanlar = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var özellik in alanlarDugum)
        {
            // Kalem dizisi alanların içine düşmüş olabilir; alan olarak yazılmaz.
            if (özellik.NameEquals("kalemler")) continue;
            alanlar[özellik.Name] = Deger(özellik.Value);
        }

        var kalemler = Kalemler(kok.Value);

        // Bilgi taşımayan kalem satırı düşürülüyor. Gerekçe TenantCatalog.DropUnusedDatasets
        // ile aynı: modelin eklediği ama hiçbir yeni bilgi taşımayan yapı, kullanıcıya
        // sunulduğunda gerçek sanılır. Gözlenen örnek: gider pusulasında model açıklamayı ve
        // net tutarı bir "kalem" satırı yaptı — ikisi de belge düzeyinde zaten vardı.
        if (kalemler.Count == 1 && !BilgiTasiyor(kalemler[0], alanlar))
        {
            kalemler = new List<IReadOnlyDictionary<string, string?>>();
            notlar.Add("Tek kalem satırı düşürüldü: değerleri belge alanlarının tekrarıydı, " +
                       "belgede kalem tablosu yok sayıldı.");
        }

        return new ExtractedDocument(alanlar, kalemler, notlar);
    }

    /// Kalem tutarlarının toplamı belge toplamıyla uyuşuyor mu?
    ///
    /// Güven ölçüsü, kapı DEĞİL: uyuşmazlık satır atlamayı ya da çift saymayı yakalar, ama
    /// uydurulan tek satır toplamın kendisine eşit olduğu için bu testi geçer — onu
    /// yukarıdaki budama kuralı yakalıyor. Uyuşmazlıkta satır atılmıyor, kullanıcıya
    /// söyleniyor: hangi okumanın doğru olduğuna belgeye bakan kişi karar verir.
    public static string? ToplamUyusmazligi(
        ExtractedDocument belge, string kalemTutarAlani, params string[] belgeToplamAlanlari)
    {
        if (belge.Items.Count == 0) return null;

        decimal toplam = 0;
        foreach (var kalem in belge.Items)
        {
            if (!kalem.TryGetValue(kalemTutarAlani, out var metin) || !Sayi(metin, out var deger))
                return null; // Kalem tutarı okunamıyorsa karşılaştırma bir şey söylemez.
            toplam += deger;
        }

        foreach (var ad in belgeToplamAlanlari)
        {
            if (belge.Fields.TryGetValue(ad, out var metin) && Sayi(metin, out var beklenen))
            {
                if (Math.Abs(beklenen - toplam) < 0.01m) return null;
                return $"Kalemlerin toplamı ({toplam.ToString(Invariant)}) belgedeki " +
                       $"{ad} değerinden ({beklenen.ToString(Invariant)}) farklı.";
            }
        }

        return null;
    }

    /// Çıkarımı var olan içe aktarma yoluna bağlar: ParsedTable üretilir, tip algılama ve
    /// satır doğrulama CSV/Excel ile aynı katmandan geçer (DatasetImportService).
    ///
    /// Kalem varsa belge düzeyi alanlar HER satıra tekrarlanır — CSV'den gelen veriyle aynı
    /// şekil, böylece pano ve doğal dilde sorgu hiçbir değişiklik istemeden çalışır.
    public static ParsedTable ToParsedTable(ExtractedDocument belge)
    {
        if (belge.Items.Count == 0)
            return new ParsedTable(
                belge.Fields.Keys.ToList(),
                new[] { belge.Fields.Values.Select(v => v ?? string.Empty).ToArray() });

        // Kalem kolonları belge kolonlarını gölgeleyebilir (ikisinde de "tutar" olabilir).
        // Kalem kazanır: satırın kendi değeri, belgenin tekrarlanan değerinden daha özeldir.
        var kalemKolonlari = belge.Items
            .SelectMany(k => k.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var belgeKolonlari = belge.Fields.Keys
            .Where(a => !kalemKolonlari.Contains(a, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var basliklar = belgeKolonlari.Concat(kalemKolonlari).ToList();
        var satirlar = new List<string[]>(belge.Items.Count);

        foreach (var kalem in belge.Items)
        {
            var satir = new string[basliklar.Count];
            for (var i = 0; i < belgeKolonlari.Count; i++)
                satir[i] = belge.Fields[belgeKolonlari[i]] ?? string.Empty;
            for (var i = 0; i < kalemKolonlari.Count; i++)
                satir[belgeKolonlari.Count + i] =
                    (kalem.TryGetValue(kalemKolonlari[i], out var d) ? d : null) ?? string.Empty;
            satirlar.Add(satir);
        }

        return new ParsedTable(basliklar, satirlar);
    }

    // ------------------------------ iç yardımcılar ------------------------------

    private static JsonElement? KokNesne(string modelText)
    {
        var temiz = Fence.Replace(modelText ?? string.Empty, string.Empty).Trim();
        var bas = temiz.IndexOf('{');
        var son = temiz.LastIndexOf('}');
        if (bas < 0 || son <= bas) return null;

        try
        {
            using var belge = JsonDocument.Parse(temiz[bas..(son + 1)]);
            return belge.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Model istenen {"alanlar": {...}} yapısını sık sık düzleştirip alanları en üste koyuyor.
    // İki yapıyı da kabul etmek gerekiyor; reddetmek çalışan bir çıkarımı biçim yüzünden
    // çöpe atmak olurdu.
    private static (IEnumerable<JsonProperty> Dugum, bool DuzYapi) AlanlarDugumu(JsonElement kok)
    {
        if (kok.ValueKind != JsonValueKind.Object)
            return (Enumerable.Empty<JsonProperty>(), false);

        if (kok.TryGetProperty("alanlar", out var alanlar) && alanlar.ValueKind == JsonValueKind.Object)
            return (alanlar.EnumerateObject(), false);

        var üst = kok.EnumerateObject()
            .Where(p => !p.NameEquals("kalemler") && !p.NameEquals("belge_turu"));
        return (üst, true);
    }

    // Kalem dizisi iki yerde olabiliyor: kökte (istenen yer) ya da `alanlar`ın içinde.
    // İkincisi gözlendi ve önemsiz değil — belgeyi kusursuz okumuş bir yanıtın kalemleri
    // yalnız kökte arandığı için tamamen kayboluyordu (ölçümde 18 hücrenin 18'i doğruydu,
    // ayrıştırıcı sıfır kalem gördü). Sessiz veri kaybı; sözleşmeye uymayan model değil,
    // katı ayrıştırıcı suçlu.
    private static List<IReadOnlyDictionary<string, string?>> Kalemler(JsonElement kok)
    {
        var sonuc = new List<IReadOnlyDictionary<string, string?>>();

        if (!TryKalemDizisi(kok, out var dizi)
            && (!kok.TryGetProperty("alanlar", out var alanlar)
                || !TryKalemDizisi(alanlar, out dizi)))
            return sonuc;

        foreach (var öge in dizi.EnumerateArray())
        {
            if (öge.ValueKind != JsonValueKind.Object) continue;
            var kalem = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var özellik in öge.EnumerateObject())
                kalem[özellik.Name] = Deger(özellik.Value);
            if (kalem.Count > 0) sonuc.Add(kalem);
        }

        return sonuc;
    }

    private static bool TryKalemDizisi(JsonElement kaynak, out JsonElement dizi)
    {
        if (kaynak.ValueKind == JsonValueKind.Object
            && kaynak.TryGetProperty("kalemler", out dizi)
            && dizi.ValueKind == JsonValueKind.Array)
            return true;

        dizi = default;
        return false;
    }

    private static string? Deger(JsonElement öge) => öge.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => Normalize(öge.GetString()),
        JsonValueKind.Number => öge.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        // İç içe nesne/dizi beklenmiyor; geldiğinde ham metni saklanıyor ki kullanıcı
        // onay ekranında ne geldiğini görebilsin.
        _ => öge.GetRawText(),
    };

    /// Bir hücreyi kanonik biçime çevirir. Sayı gibi görünüyorsa nokta ondalıklı metin,
    /// değilse kırpılmış özgün metin döner.
    ///
    /// Bu adım şart, çünkü model biçim kuralını tutmuyor: aynı yanıtta "721,83" ve "1993.32"
    /// yan yana geldi. Karışık kolonu ValueFormats'a olduğu gibi vermek SESSİZ bozulma
    /// üretir — virgülü gören algılayıcı kolonu Türkçe sayar, sonra "1993.32" tr-TR'de
    /// 199332 olarak ayrıştırılır (nokta binlik ayıracı sanılır), yani yüz kat sapma.
    /// CSV'de bu tehlike yok: orada bir kolonu tek araç yazar, biçim tutarlıdır.
    ///
    /// Tarih bilerek dokunulmuyor. "04/03/2026" gibi belirsiz bir tarihi burada çevirmek
    /// gün-ay sırasını rastlantıya bırakır; olduğu gibi bırakılırsa kolon en kötü durumda
    /// "text" algılanır — yanlış tarih sessizdir, metin kolon görünürdür.
    public static string? Normalize(string? ham)
    {
        if (ham is null) return null;
        var metin = ham.Trim();
        if (metin.Length == 0) return null;

        var çıplak = Susler.Replace(metin, string.Empty).Trim();
        if (çıplak.Length == 0) return metin;

        if (Integer.IsMatch(çıplak)) return çıplak;

        if (Thousands.IsMatch(çıplak))
            return çıplak.Replace(".", string.Empty).Replace(",", string.Empty);

        if (Decimal1.IsMatch(çıplak))
            return çıplak.Replace(',', '.');

        // İki ayraç birlikte: sonda kalan ondalıktır ("14.849,57" / "14,849.57").
        var virgul = çıplak.LastIndexOf(',');
        var nokta = çıplak.LastIndexOf('.');
        if (virgul >= 0 && nokta >= 0)
        {
            var ondalik = virgul > nokta ? ',' : '.';
            var binlik = ondalik == ',' ? '.' : ',';
            var aday = çıplak.Replace(binlik.ToString(), string.Empty).Replace(ondalik, '.');
            if (Decimal1.IsMatch(aday) || Integer.IsMatch(aday)) return aday;
        }

        return metin;
    }

    internal static bool Sayi(string? metin, out decimal sonuc)
    {
        sonuc = 0;
        var normal = Normalize(metin);
        return normal is not null
            && decimal.TryParse(normal, NumberStyles.Number, Invariant, out sonuc);
    }

    // Bir kalem satırının kendine ait bilgisi var mı? Belge düzeyinde zaten bulunan
    // değerleri tekrar etmek bilgi değil.
    private static bool BilgiTasiyor(
        IReadOnlyDictionary<string, string?> kalem, IReadOnlyDictionary<string, string?> alanlar)
    {
        var belgeDegerleri = alanlar.Values
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return kalem.Values.Any(d => !string.IsNullOrWhiteSpace(d) && !belgeDegerleri.Contains(d!.Trim()));
    }
}
