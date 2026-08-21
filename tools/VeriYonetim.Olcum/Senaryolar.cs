using Npgsql;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Olcum;

// Veritabanı üzerinde ölçülen tek bir sorgu. SQL'i BU ARAÇ YAZMAZ: uygulamanın
// builder'ları üretir (DatasetRowQueryBuilder / DatasetAggregateQueryBuilder), böylece
// ölçülen metin ile canlıda çalışan metin aynı olur.
internal record SqlSenaryo(string Ad, string Aciklama, string Sql,
    IReadOnlyList<NpgsqlParameter> Parametreler);

// HTTP ucundan ölçülen tek bir istek. Sayfalı satır listesinin SQL'i controller'da
// kurulduğu için o yol yalnız uçtan ölçülebilir — ve zaten tarayıcının gördüğü süre bu.
internal record UcSenaryo(string Ad, string Aciklama, string Yol);

internal static class Senaryolar
{
    public const int SayfaBoyu = 25;

    // Ölçülen sorguların hepsi TEK bir veri setine bakar; tabloda başka setler ve başka
    // firmalar da vardır. Ayrım önemli: ölçülen şey "tablodaki 2 milyon satır" değil,
    // "2 milyon satırın içinden bir firmanın N satırını bulmak".
    public static List<SqlSenaryo> Sql(Guid setId, string setAdi, int satirSayisi,
        Guid musteriSetId)
    {
        var sema = Ortam.Sema(Ortam.SatisSemasi);
        var kapsam = QueryScope.Single(setId, setAdi, sema);

        var senaryolar = new List<SqlSenaryo>();

        void Satir(string ad, string aciklama, RowQuery sorgu)
        {
            var kurulu = DatasetRowQueryBuilder.BuildSelect(sorgu, kapsam);
            senaryolar.Add(new SqlSenaryo(ad, aciklama, kurulu.Sql, kurulu.Parameters));
        }

        void Ozet(string ad, string aciklama, AggregateQuery sorgu, QueryScope? kendiKapsami = null)
        {
            var kurulu = DatasetAggregateQueryBuilder.Build(sorgu, kendiKapsami ?? kapsam);
            senaryolar.Add(new SqlSenaryo(ad, aciklama, kurulu.Sql, kurulu.Parameters));
        }

        // --- Satır listesi yolları -------------------------------------------------
        Satir("satir_ilk_sayfa", "İlk sayfa, tarihe göre sıralı",
            new RowQuery(1, SayfaBoyu, "tarih", "desc", []));

        // Son sayfa bilinçli: OFFSET büyüdükçe PostgreSQL atlanan satırları da üretmek
        // zorundadır. Derin sayfalamanın bedeli varsa burada görünür.
        Satir("satir_son_sayfa", "Son sayfa (derin OFFSET)",
            new RowQuery(Math.Max(satirSayisi / SayfaBoyu, 1), SayfaBoyu, "tarih", "desc", []));

        Satir("filtre_metin", "sehir = Ankara",
            new RowQuery(1, SayfaBoyu, "tarih", "desc", [new RowFilter("sehir", "eq", "Ankara")]));

        Satir("filtre_metin_icinde", "urun içinde 'kablo'",
            new RowQuery(1, SayfaBoyu, "tarih", "desc", [new RowFilter("urun", "contains", "kablo")]));

        Satir("filtre_sayi", "tutar >= 150000",
            new RowQuery(1, SayfaBoyu, "tutar", "desc", [new RowFilter("tutar", "gte", "150000")]));

        Satir("filtre_donem", "tarih son 90 gün",
            new RowQuery(1, SayfaBoyu, "tarih", "desc", [new RowFilter("tarih", "inPeriod", "son90Gun")]));

        // --- Özet (agregasyon) yolları ---------------------------------------------
        Ozet("ozet_sehir", "Şehre göre toplam tutar",
            new AggregateQuery(
                GroupBy: ["sehir"], Metrics: [new MetricSpec("sum", "tutar")],
                Bucket: null, Sort: "value", Dir: "desc", Limit: 20, Filters: []));

        Ozet("ozet_iki_anahtar", "Şehir × kategori toplam tutar",
            new AggregateQuery(
                GroupBy: ["sehir", "kategori"], Metrics: [new MetricSpec("sum", "tutar")],
                Bucket: null, Sort: "value", Dir: "desc", Limit: 50, Filters: []));

        Ozet("zaman_serisi_ay", "Aylara göre toplam tutar",
            new AggregateQuery(
                GroupBy: ["tarih"], Metrics: [new MetricSpec("sum", "tutar")],
                Bucket: "month", Sort: "key", Dir: "asc", Limit: null, Filters: []));

        Ozet("medyan_sehir", "Şehre göre medyan tutar",
            new AggregateQuery(
                GroupBy: ["sehir"], Metrics: [new MetricSpec("median", "tutar")],
                Bucket: null, Sort: "value", Dir: "desc", Limit: 20, Filters: []));

        Ozet("farkli_musteri", "Kaç farklı müşteri (gruplamasız)",
            new AggregateQuery(
                GroupBy: [], Metrics: [new MetricSpec("countDistinct", "musteri_kodu")],
                Bucket: null, Sort: null, Dir: null, Limit: null, Filters: []));

        Ozet("ozet_filtreli", "Son 90 günün kategori özeti",
            new AggregateQuery(
                GroupBy: ["kategori"], Metrics: [new MetricSpec("sum", "tutar")],
                Bucket: null, Sort: "value", Dir: "desc", Limit: 20,
                Filters: [new RowFilter("tarih", "inPeriod", "son90Gun")]));

        // --- İki veri setli JOIN ----------------------------------------------------
        // Bu yolun querystring ucu YOK: iki seti birleştiren sorgu yalnız doğal dil
        // planından doğar. Ölçülen SQL, o planın ürettiği SQL'in ta kendisi.
        var joinKapsami = new QueryScope(
            [
                new QuerySource("d0", setId, setAdi, sema),
                new QuerySource("d1", musteriSetId, Ortam.MusteriSeti, Ortam.Sema(Ortam.MusteriSemasi))
            ],
            [new QueryJoin("d0", "musteri_kodu", "d1", "musteri_kodu")]);

        Ozet("join_segment", "Müşteri segmentine göre toplam tutar (2 set)",
            new AggregateQuery(
                GroupBy: [$"{Ortam.MusteriSeti}.segment"],
                Metrics: [new MetricSpec("sum", $"{setAdi}.tutar")],
                Bucket: null, Sort: "value", Dir: "desc", Limit: 20, Filters: []),
            joinKapsami);

        return senaryolar;
    }

    // Uçtan ölçülenler: tarayıcının gerçekten çağırdığı adresler. Satır listesi ucu her
    // çağrıda ayrıca COUNT(*) da koşturur (sayfa sayısı için) — o maliyet de bu ölçümün
    // içinde, çünkü kullanıcı ikisini birden bekliyor.
    public static List<UcSenaryo> Uc(Guid setId, int satirSayisi)
    {
        var sonSayfa = Math.Max(satirSayisi / SayfaBoyu, 1);
        var temel = $"/api/datasets/{setId}";

        return
        [
            new UcSenaryo("uc_satir_ilk_sayfa", "GET rows — ilk sayfa",
                $"{temel}/rows?page=1&pageSize={SayfaBoyu}&sort=tarih&dir=desc"),

            new UcSenaryo("uc_satir_son_sayfa", "GET rows — son sayfa",
                $"{temel}/rows?page={sonSayfa}&pageSize={SayfaBoyu}&sort=tarih&dir=desc"),

            new UcSenaryo("uc_filtre_metin", "GET rows — sehir = Ankara",
                $"{temel}/rows?page=1&pageSize={SayfaBoyu}&filter=sehir%3Aeq%3AAnkara"),

            new UcSenaryo("uc_ozet_sehir", "GET aggregate — şehre göre toplam",
                $"{temel}/aggregate?groupBy=sehir&op=sum&metric=tutar"),

            new UcSenaryo("uc_zaman_serisi", "GET aggregate — aylık toplam",
                $"{temel}/aggregate?groupBy=tarih&bucket=month&op=sum&metric=tutar")
        ];
    }
}
