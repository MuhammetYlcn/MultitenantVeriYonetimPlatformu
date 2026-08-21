namespace VeriYonetim.Olcum;

// Ölçüm verisini üretir. Tohumlu (deterministik) rastgelelik: aynı tohumla aynı veri
// çıkar, yani iki ölçüm koşusu birbiriyle kıyaslanabilir. Rastgele veriyle her koşuda
// başka bir tablo ölçülürdü ve "indeksten sonra hızlandı" cümlesi kurulamazdı.
internal sealed class Uretici
{
    // Şehir dağılımı bilinçli olarak ÇARPIK: gerçek veride de satışların yarısı birkaç
    // şehirde toplanır. Düz dağılımda gruplama sorgusu gerçekte olmayan bir kolaylık
    // görürdü (her grup eşit boy, hiçbir grup baskın değil).
    private static readonly string[] Sehirler =
    {
        "İstanbul", "Ankara", "İzmir", "Bursa", "Antalya", "Adana", "Konya", "Gaziantep",
        "Kayseri", "Mersin", "Eskişehir", "Samsun", "Denizli", "Trabzon", "Malatya",
        "Erzurum", "Van", "Aydın", "Balıkesir", "Sakarya"
    };

    private static readonly string[] Kategoriler =
    {
        "Elektronik", "Kırtasiye", "Mobilya", "Gıda", "Temizlik", "Tekstil", "Yedek Parça", "Hizmet"
    };

    // Bir kısmı bilerek "kablo" içeriyor: metin içinde arama (ILIKE '%kablo%') ölçülecek.
    private static readonly string[] Urunler =
    {
        "HDMI Kablo", "USB Kablo", "Ağ Kablosu", "Uzatma Kablosu", "Kablo Kanalı",
        "Klavye", "Fare", "Monitör", "Yazıcı", "Toner", "A4 Kağıt", "Dosya Klasörü",
        "Kalem Seti", "Ofis Koltuğu", "Çalışma Masası", "Dolap", "Sehpa", "Aydınlatma",
        "Filtre Kahve", "Çay", "Su Damacana", "Şeker", "Deterjan", "Kağıt Havlu",
        "Eldiven", "İş Önlüğü", "Baret", "Rulman", "Kayış", "Bakım Hizmeti"
    };

    private static readonly string[] Segmentler = { "Kurumsal", "KOBİ", "Bayi", "Perakende" };

    private readonly Random _rastgele;
    private readonly DateTime _baslangic;

    public Uretici(int tohum)
    {
        _rastgele = new Random(tohum);
        // Veri bugünden geriye üç yıla yayılır: "son 90 gün" gibi göreli dönem filtreleri
        // ölçülebilsin ve seçici (satırların küçük bir kısmını tutan) olsun diye.
        _baslangic = DateTime.Today.AddYears(-3);
    }

    public Dictionary<string, object?> Satis(int sira)
    {
        var gun = _rastgele.Next(0, 3 * 365);
        var tarih = _baslangic.AddDays(gun);

        var miktar = _rastgele.Next(1, 101);
        var birimFiyat = Math.Round((decimal)(_rastgele.NextDouble() * 1995 + 5), 2);

        return new Dictionary<string, object?>
        {
            ["fatura_no"] = $"F{tarih.Year}{sira + 1:D7}",
            ["tarih"] = tarih,
            ["sehir"] = Carpik(Sehirler),
            ["kategori"] = Kategoriler[_rastgele.Next(Kategoriler.Length)],
            ["musteri_kodu"] = $"M{_rastgele.Next(1, Ortam.MusteriSayisi + 1):D5}",
            ["urun"] = Urunler[_rastgele.Next(Urunler.Length)],
            ["miktar"] = (decimal)miktar,
            ["birim_fiyat"] = birimFiyat,
            ["tutar"] = Math.Round(miktar * birimFiyat, 2)
        };
    }

    public Dictionary<string, object?> Musteri(int sira) => new()
    {
        ["musteri_kodu"] = $"M{sira + 1:D5}",
        ["unvan"] = $"{Carpik(Sehirler)} {Urunler[_rastgele.Next(Urunler.Length)]} Ltd. Şti. {sira + 1}",
        ["sehir"] = Carpik(Sehirler),
        ["segment"] = Segmentler[_rastgele.Next(Segmentler.Length)]
    };

    // Kareyle çarpıtma: baştaki değerler belirgin biçimde daha sık seçilir.
    private string Carpik(string[] secenekler)
    {
        var oran = _rastgele.NextDouble();
        var indeks = (int)(oran * oran * secenekler.Length);

        return secenekler[Math.Min(indeks, secenekler.Length - 1)];
    }
}
