using VeriYonetim.Api.Services;

namespace VeriYonetim.TrainingData;

// Tek bir kolon: şemadaki hâli + Türkçe cümle kurmak için gereken ekli biçimleri.
//
// Neden üç ayrı biçim tutuluyor? Türkçe'de ek, kelimenin son ünlüsüne göre değişir
// ("şehre" ama "kategoriye"). Kod içinde ek üretmeye çalışmak yerine doğru biçimler
// elle yazıldı: veri girişi bir kerelik, üretilen cümlenin bozuk olması ise kalıcı.
//   Label     "şehir"        → yalın hâli
//   LabelPoss "şehri"        → "şehri Ankara olan satışlar"
//   ByPhrase  "şehre göre"   → "şehre göre toplam tutar"
// Values: metin kolonunda gerçekçi değerler, sayı kolonunda gerçekçi eşikler.
//
// IsId: kimlik kolonu mu (fatura no, cari kod...). Kimliğe GÖRE gruplamak anlamsız bir
// soru üretir ("fatura numarasına göre toplam tutar" — her grupta tek satır). Filtrede
// ise gayet kullanışlıdır ("F-1001 numaralı fatura"), o yüzden tümden dışlanmıyor.
public record ColumnDef(
    string Name,
    string Type,
    string Label,
    string LabelPoss,
    string ByPhrase,
    string[] Values,
    bool IsId = false);

// Bir veri seti: şemadaki adı + sorularda geçecek özne biçimleri.
//   Singular "satış", Plural "satışlar", Genitive "satışların"
public record DatasetDef(
    string Name,
    string Description,
    string Singular,
    string Plural,
    string Genitive,
    ColumnDef[] Columns);

public record RelationDef(string FromDataset, string FromColumn, string ToDataset, string ToColumn);

// Bir firmanın tüm dünyası. HoldOut=true olan kataloglar EĞİTİME HİÇ GİRMEZ, yalnız
// değerlendirmede kullanılır: asıl ölçmek istediğimiz şey modelin ezberlediği şemada
// değil, ilk kez gördüğü bir şemada plan üretebilmesi. Gerçek kullanımda her firmanın
// kolon adları farklıdır; ezberi ölçen bir doğruluk sayısı hiçbir şey söylemez.
public record CatalogDef(
    string Name,
    DatasetDef[] Datasets,
    RelationDef[] Relations,
    bool HoldOut = false)
{
    // Üretecin kullandığı sözlükleri, çalışma anındaki gerçek TenantCatalog'a çevirir.
    // Kimlikler ada göre türetiliyor ki aynı katalog her koşuda aynı Guid'leri alsın.
    public TenantCatalog ToCatalog()
    {
        var ids = Datasets.ToDictionary(d => d.Name, d => DeterministicId(Name + "/" + d.Name));

        var infos = Datasets
            .Select(d => new DatasetInfo(
                ids[d.Name],
                d.Name,
                d.Description,
                d.Columns.ToDictionary(c => c.Name, c => c.Type),
                RowCount: 1000))
            .ToList();

        var relations = Relations
            .Select(r => new RelationInfo(
                ids[r.FromDataset], r.FromColumn, ids[r.ToDataset], r.ToColumn))
            .ToList();

        return new TenantCatalog(infos, relations);
    }

    private static Guid DeterministicId(string key)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(key));
        return new Guid(bytes);
    }
}

public static class CatalogDefs
{
    // Kısaltmalar: tanımları okunur tutmak için.
    private static ColumnDef T(string name, string label, string poss, string by, params string[] values)
        => new(name, "text", label, poss, by, values);

    // Kimlik kolonu: filtrede kullanılır, gruplamada kullanılmaz.
    private static ColumnDef Id(string name, string label, string poss, string by, params string[] values)
        => new(name, "text", label, poss, by, values, IsId: true);

    private static ColumnDef N(string name, string label, string poss, string by, params string[] thresholds)
        => new(name, "number", label, poss, by, thresholds);

    private static ColumnDef D(string name, string label, string poss, string by)
        => new(name, "date", label, poss, by, Array.Empty<string>());

    public static readonly IReadOnlyList<CatalogDef> All = new[]
    {
        // --- 1. Perakende: iki set + ilişki, üstelik "sehir" İKİ sette birden var.
        // Bu bilinçli: model "sehir" görünce hangi setten alacağına soruya bakarak
        // karar vermeyi öğrenmeli.
        new CatalogDef("Perakende",
            new[]
            {
                new DatasetDef("Satislar", "Mağaza satış kayıtları", "satış", "satışlar", "satışların",
                    new[]
                    {
                        Id("satis_no", "satış no", "satış numarası", "satış numarasına göre", "S-1001", "S-1002"),
                        Id("musteri_no", "müşteri no", "müşteri numarası", "müşteri numarasına göre", "M-01", "M-02"),
                        T("urun", "ürün", "ürünü", "ürüne göre", "Klavye", "Monitör", "Fare", "Kulaklık", "Yazıcı"),
                        T("kategori", "kategori", "kategorisi", "kategoriye göre", "Bilgisayar", "Aksesuar", "Kırtasiye", "Mobilya"),
                        T("sehir", "şehir", "şehri", "şehre göre", "Ankara", "İstanbul", "İzmir", "Bursa", "Antalya"),
                        N("tutar", "tutar", "tutarı", "tutara göre", "500", "1000", "2500", "5000"),
                        N("adet", "adet", "adedi", "adede göre", "5", "10", "50"),
                        D("tarih", "tarih", "tarihi", "tarihe göre")
                    }),
                new DatasetDef("Musteriler", "Müşteri kartları", "müşteri", "müşteriler", "müşterilerin",
                    new[]
                    {
                        Id("no", "müşteri no", "numarası", "numaraya göre", "M-01", "M-02"),
                        T("ad", "ad", "adı", "ada göre", "Ahmet Yılmaz", "Ayşe Demir", "Mehmet Kaya"),
                        T("sehir", "şehir", "şehri", "şehre göre", "Ankara", "İstanbul", "İzmir", "Konya"),
                        T("segment", "segment", "segmenti", "segmente göre", "Bireysel", "Kurumsal", "Bayi"),
                        D("kayit_tarihi", "kayıt tarihi", "kayıt tarihi", "kayıt tarihine göre")
                    })
            },
            new[] { new RelationDef("Satislar", "musteri_no", "Musteriler", "no") }),

        // --- 2. Muhasebe: kolon adları kısaltmalı ve setin adı sorunun konusuyla
        // birebir örtüşmüyor ("cari" = müşteri/tedarikçi).
        new CatalogDef("Muhasebe",
            new[]
            {
                new DatasetDef("Faturalar", "Kesilen satış faturaları", "fatura", "faturalar", "faturaların",
                    new[]
                    {
                        Id("fatura_no", "fatura no", "fatura numarası", "fatura numarasına göre", "F-1001", "F-1002"),
                        Id("cari_kod", "cari kod", "cari kodu", "cari koduna göre", "C-100", "C-200"),
                        N("tutar", "tutar", "tutarı", "tutara göre", "1000", "10000", "50000"),
                        N("kdv", "KDV", "KDV'si", "KDV'ye göre", "180", "1800"),
                        T("durum", "durum", "durumu", "duruma göre", "Ödendi", "Bekliyor", "İptal", "Gecikmiş"),
                        D("tarih", "tarih", "tarihi", "tarihe göre")
                    }),
                new DatasetDef("Cariler", "Cari hesap kartları", "cari", "cariler", "carilerin",
                    new[]
                    {
                        Id("kod", "kod", "kodu", "koda göre", "C-100", "C-200"),
                        T("unvan", "unvan", "unvanı", "unvana göre", "Yılmaz Ltd.", "Demir A.Ş.", "Kaya Ticaret"),
                        T("il", "il", "ili", "il bazında", "Ankara", "İstanbul", "İzmir", "Adana"),
                        T("temsilci", "temsilci", "temsilcisi", "temsilciye göre", "Ali Vural", "Zeynep Ak")
                    })
            },
            new[] { new RelationDef("Faturalar", "cari_kod", "Cariler", "kod") }),

        // --- 3. Depo: tek set, ilişki yok. "Bu iki seti birleştir" sorularının
        // unsupported dönmesi gereken durumlar buradan üretiliyor.
        new CatalogDef("Depo",
            new[]
            {
                new DatasetDef("Stok", "Depo stok durumu", "ürün", "ürünler", "ürünlerin",
                    new[]
                    {
                        Id("urun_kodu", "ürün kodu", "ürün kodu", "ürün koduna göre", "U-01", "U-02"),
                        T("urun_adi", "ürün adı", "ürün adı", "ürün adına göre", "Vida", "Somun", "Rulman", "Conta"),
                        T("kategori", "kategori", "kategorisi", "kategoriye göre", "Bağlantı", "Hareket", "Sızdırmazlık"),
                        T("depo", "depo", "deposu", "depoya göre", "Merkez", "Şube-1", "Şube-2"),
                        N("miktar", "miktar", "miktarı", "miktara göre", "10", "100", "500"),
                        N("birim_fiyat", "birim fiyat", "birim fiyatı", "birim fiyata göre", "5", "25", "150"),
                        D("son_giris", "son giriş", "son giriş tarihi", "son giriş tarihine göre")
                    })
            },
            Array.Empty<RelationDef>()),

        // --- 4. İnsan kaynakları: sayısal kolonu "maaş" — medyan sorularının
        // en doğal yeri (ortalama maaş yanıltıcıdır, medyan istenir).
        new CatalogDef("InsanKaynaklari",
            new[]
            {
                new DatasetDef("Personel", "Çalışan kayıtları", "personel", "personeller", "personelin",
                    new[]
                    {
                        Id("sicil", "sicil", "sicili", "sicile göre", "P-001", "P-002"),
                        T("ad_soyad", "ad soyad", "adı", "ada göre", "Ahmet Yılmaz", "Elif Şahin"),
                        T("departman", "departman", "departmanı", "departmana göre", "Satış", "Üretim", "Muhasebe", "Bilgi İşlem", "İnsan Kaynakları"),
                        T("unvan", "unvan", "unvanı", "unvana göre", "Uzman", "Müdür", "Şef", "Stajyer"),
                        T("sehir", "şehir", "şehri", "şehre göre", "Ankara", "İstanbul", "Kocaeli"),
                        N("maas", "maaş", "maaşı", "maaşa göre", "25000", "40000", "75000"),
                        D("ise_giris", "işe giriş", "işe giriş tarihi", "işe giriş tarihine göre")
                    })
            },
            Array.Empty<RelationDef>()),

        // --- 5. Sipariş + kargo: iki tarih kolonu olan set (hangisinin sorulduğuna
        // dikkat etmeyi öğrenmeli).
        new CatalogDef("Siparis",
            new[]
            {
                new DatasetDef("Siparisler", "Müşteri siparişleri", "sipariş", "siparişler", "siparişlerin",
                    new[]
                    {
                        Id("siparis_no", "sipariş no", "sipariş numarası", "sipariş numarasına göre", "SP-01", "SP-02"),
                        T("musteri", "müşteri", "müşterisi", "müşteriye göre", "Yılmaz Ltd.", "Demir A.Ş."),
                        T("urun", "ürün", "ürünü", "ürüne göre", "Masa", "Sandalye", "Dolap"),
                        T("durum", "durum", "durumu", "duruma göre", "Hazırlanıyor", "Kargoda", "Teslim Edildi", "İptal"),
                        N("adet", "adet", "adedi", "adede göre", "1", "5", "20"),
                        N("tutar", "tutar", "tutarı", "tutara göre", "750", "3000", "12000"),
                        D("siparis_tarihi", "sipariş tarihi", "sipariş tarihi", "sipariş tarihine göre"),
                        D("teslim_tarihi", "teslim tarihi", "teslim tarihi", "teslim tarihine göre")
                    }),
                new DatasetDef("Kargolar", "Kargo gönderileri", "gönderi", "gönderiler", "gönderilerin",
                    new[]
                    {
                        Id("takip_no", "takip no", "takip numarası", "takip numarasına göre", "K-9001", "K-9002"),
                        Id("siparis_no", "sipariş no", "sipariş numarası", "sipariş numarasına göre", "SP-01", "SP-02"),
                        T("firma", "kargo firması", "firması", "kargo firmasına göre", "Hızlı Kargo", "Güven Kargo"),
                        N("ucret", "ücret", "ücreti", "ücrete göre", "50", "120", "300"),
                        D("cikis_tarihi", "çıkış tarihi", "çıkış tarihi", "çıkış tarihine göre")
                    })
            },
            new[] { new RelationDef("Siparisler", "siparis_no", "Kargolar", "siparis_no") }),

        // --- 6. Üretim: iki sayısal kolon (üretilen / fire) — "hangi ölçüm sorulmuş"
        // ayrımını zorlar.
        new CatalogDef("Uretim",
            new[]
            {
                new DatasetDef("UretimKayitlari", "Vardiya üretim kayıtları", "kayıt", "kayıtlar", "kayıtların",
                    new[]
                    {
                        Id("parti_no", "parti no", "parti numarası", "parti numarasına göre", "PT-01", "PT-02"),
                        T("hat", "hat", "hattı", "hatta göre", "Hat-1", "Hat-2", "Hat-3"),
                        T("urun", "ürün", "ürünü", "ürüne göre", "Profil", "Levha", "Boru"),
                        T("vardiya", "vardiya", "vardiyası", "vardiyaya göre", "Sabah", "Akşam", "Gece"),
                        N("uretilen", "üretilen adet", "üretilen adedi", "üretilen adede göre", "100", "1000", "5000"),
                        N("fire", "fire", "firesi", "fireye göre", "5", "20", "100"),
                        D("tarih", "tarih", "tarihi", "tarihe göre")
                    })
            },
            Array.Empty<RelationDef>()),

        // --- 7. Gider
        new CatalogDef("Gider",
            new[]
            {
                new DatasetDef("Giderler", "Masraf kayıtları", "gider", "giderler", "giderlerin",
                    new[]
                    {
                        Id("kayit_no", "kayıt no", "kayıt numarası", "kayıt numarasına göre", "G-01", "G-02"),
                        T("kalem", "kalem", "kalemi", "kaleme göre", "Kira", "Elektrik", "Yakıt", "Kırtasiye", "Danışmanlık"),
                        T("departman", "departman", "departmanı", "departmana göre", "Satış", "Üretim", "Yönetim"),
                        T("odeme_tipi", "ödeme tipi", "ödeme tipi", "ödeme tipine göre", "Nakit", "Kredi Kartı", "Havale"),
                        N("tutar", "tutar", "tutarı", "tutara göre", "1000", "7500", "20000"),
                        D("tarih", "tarih", "tarihi", "tarihe göre")
                    })
            },
            Array.Empty<RelationDef>()),

        // --- 8. Servis: ilişkili ikinci set, kolon adları ilkinden farklı.
        new CatalogDef("Servis",
            new[]
            {
                new DatasetDef("ServisKayitlari", "Teknik servis iş emirleri", "iş emri", "iş emirleri", "iş emirlerinin",
                    new[]
                    {
                        Id("kayit_no", "kayıt no", "kayıt numarası", "kayıt numarasına göre", "IS-01", "IS-02"),
                        Id("musteri_kodu", "müşteri kodu", "müşteri kodu", "müşteri koduna göre", "MK-1", "MK-2"),
                        T("cihaz", "cihaz", "cihazı", "cihaza göre", "Kombi", "Klima", "Buzdolabı"),
                        T("ariza_tipi", "arıza tipi", "arıza tipi", "arıza tipine göre", "Elektrik", "Mekanik", "Yazılım", "Bakım"),
                        N("sure_dk", "süre", "süresi", "süreye göre", "30", "60", "180"),
                        N("ucret", "ücret", "ücreti", "ücrete göre", "250", "800", "1500"),
                        D("tarih", "tarih", "tarihi", "tarihe göre")
                    }),
                new DatasetDef("Abone", "Servis abonelikleri", "abone", "aboneler", "abonelerin",
                    new[]
                    {
                        Id("kod", "kod", "kodu", "koda göre", "MK-1", "MK-2"),
                        T("unvan", "unvan", "unvanı", "unvana göre", "Aksu Sitesi", "Beyaz Plaza"),
                        T("bolge", "bölge", "bölgesi", "bölgeye göre", "Kuzey", "Güney", "Merkez"),
                        T("paket", "paket", "paketi", "pakete göre", "Standart", "Genişletilmiş")
                    })
            },
            new[] { new RelationDef("ServisKayitlari", "musteri_kodu", "Abone", "kod") }),

        // ================= DEĞERLENDİRME İÇİN AYRILANLAR =================
        // Bu iki katalog eğitim verisine HİÇ girmez. Model bu şemaları ilk kez
        // değerlendirmede görür; ölçtüğümüz şey ezber değil genelleme olur.

        // --- 9. Filo (yalnız değerlendirme)
        new CatalogDef("Filo",
            new[]
            {
                new DatasetDef("Araclar", "Şirket araç filosu", "araç", "araçlar", "araçların",
                    new[]
                    {
                        T("plaka", "plaka", "plakası", "plakaya göre", "06 ABC 123", "34 XYZ 789"),
                        T("marka", "marka", "markası", "markaya göre", "Ford", "Renault", "Fiat"),
                        T("departman", "departman", "departmanı", "departmana göre", "Satış", "Servis", "Yönetim"),
                        T("surucu", "sürücü", "sürücüsü", "sürücüye göre", "Kemal Öz", "Nur Ateş"),
                        N("km", "kilometre", "kilometresi", "kilometreye göre", "10000", "80000", "200000"),
                        N("yakit_lt", "yakıt", "yakıtı", "yakıta göre", "30", "60", "120"),
                        D("tarih", "tarih", "tarihi", "tarihe göre")
                    })
            },
            Array.Empty<RelationDef>(),
            HoldOut: true),

        // --- 10. Eğitim/kurs (yalnız değerlendirme) — ilişkili, iki setli.
        new CatalogDef("Kurs",
            new[]
            {
                new DatasetDef("Katilimlar", "Kurs katılım kayıtları", "katılım", "katılımlar", "katılımların",
                    new[]
                    {
                        Id("kayit_no", "kayıt no", "kayıt numarası", "kayıt numarasına göre", "KT-01", "KT-02"),
                        Id("kursiyer_kodu", "kursiyer kodu", "kursiyer kodu", "kursiyer koduna göre", "KS-1", "KS-2"),
                        T("kurs", "kurs", "kursu", "kursa göre", "Excel", "Muhasebe", "İngilizce"),
                        T("sehir", "şehir", "şehri", "şehre göre", "Ankara", "İzmir", "Samsun"),
                        N("ucret", "ücret", "ücreti", "ücrete göre", "500", "2500", "6000"),
                        N("sure_saat", "süre", "süresi", "süreye göre", "8", "24", "60"),
                        D("tarih", "tarih", "tarihi", "tarihe göre")
                    }),
                new DatasetDef("Kursiyerler", "Kursiyer kartları", "kursiyer", "kursiyerler", "kursiyerlerin",
                    new[]
                    {
                        Id("kod", "kod", "kodu", "koda göre", "KS-1", "KS-2"),
                        T("ad", "ad", "adı", "ada göre", "Selin Ay", "Burak Tan"),
                        T("firma", "firma", "firması", "firmaya göre", "Aksu Ltd.", "Beyaz A.Ş."),
                        T("seviye", "seviye", "seviyesi", "seviyeye göre", "Başlangıç", "Orta", "İleri")
                    })
            },
            new[] { new RelationDef("Katilimlar", "kursiyer_kodu", "Kursiyerler", "kod") },
            HoldOut: true)
    };

    public static IEnumerable<CatalogDef> Training => All.Where(c => !c.HoldOut);
    public static IEnumerable<CatalogDef> Evaluation => All.Where(c => c.HoldOut);
}
