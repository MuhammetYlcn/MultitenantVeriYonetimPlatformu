using System.Diagnostics;
using Npgsql;

namespace VeriYonetim.Olcum;

// Denenen tek bir indeks: adı, kuran SQL'i, hangi senaryoyu hedeflediği.
internal record IndeksAdayi(string Ad, string Sql, string Hedef, string Gerekce)
{
    // Kurulamayan aday da bir sonuçtur: NEDEN kurulamadığı, kurulabilenler kadar
    // öğreticidir (bkz. tarih adayı).
    public string? Hata { get; set; }
    public double SaniyeKurulum { get; set; }
    public string Boyut { get; set; } = "—";
}

// İfade indeksi denemeleri.
//
// 21.08 ölçümünün 2. bulgusu: 1 milyon satırlık bir veri setinde PostgreSQL "DatasetId"
// indeksinden vazgeçiyor ve tabloyu baştan tarıyor. Doğru bir karar — ölçüm ortamında o
// set tablonun ~%45'i, yarım tabloyu indeksten okumak taramadan pahalıdır. Ama sonucu şu:
// 25 satır döndüren `sehir = 'Ankara'` sorgusu 1 milyon satır okuyor.
//
// Buradaki adaylar bunu kırmayı deniyor: filtre değerinin KENDİSİNİ indekslersek, veri
// setinin tablonun ne kadarını kapladığı önemsizleşir — arama doğrudan seçici olur.
//
// İndekslenen ifade, sorgunun ürettiği ifadeyle KARAKTER KARAKTER aynı olmalıdır; yoksa
// PostgreSQL indeksi görmez ve indeks sessizce boşa yatırım olur. Bu yüzden adayların
// ifadeleri DatasetSqlExpr'den okunarak yazıldı, akıldan değil.
internal static class Indeksler
{
    // Adayların hepsi "DatasetId" ile BAŞLIYOR. Sebebi izolasyon: her sorgu önce tek bir
    // veri setine daralır, filtre ondan sonra gelir. Kolon tek başına indekslenseydi
    // indeks bütün firmaların değerlerini tek ağaçta toplardı ve arama yine setle
    // kesişmek zorunda kalırdı.
    public static IReadOnlyList<IndeksAdayi> Adaylar() =>
    [
        new("ix_olcum_sehir",
            """
            CREATE INDEX ix_olcum_sehir ON "DatasetRows"
              ("DatasetId", lower(("Data"->>'sehir')))
            """,
            "filtre_metin",
            "Metin eşitliği `lower(...) = lower(@p)` üretiyor (harfe duyarsız arama " +
            "kararı); indeks de lower() üzerine kurulmalı, ham değere kurulan indeks " +
            "bu sorguda kullanılmaz."),

        new("ix_olcum_tutar",
            """
            CREATE INDEX ix_olcum_tutar ON "DatasetRows"
              ("DatasetId", ((("Data"->>'tutar'))::numeric))
            """,
            "filtre_sayi",
            "Sayısal karşılaştırma `(...)::numeric >= @p` üretiyor. text→numeric " +
            "dönüşümü IMMUTABLE olduğu için indekslenebiliyor."),

        new("ix_olcum_urun_trgm",
            """
            CREATE INDEX ix_olcum_urun_trgm ON "DatasetRows"
              USING gin ("DatasetId", ("Data"->>'urun') gin_trgm_ops)
            """,
            "filtre_metin_icinde",
            "`ILIKE '%kablo%'` baştan bağlı olmadığı için b-tree işe yaramaz; üç harfli " +
            "parçalara (trigram) bakan GIN gerekir. \"DatasetId\"in aynı indekse " +
            "girebilmesi için btree_gin uzantısı şart."),

        new("ix_olcum_tarih",
            """
            CREATE INDEX ix_olcum_tarih ON "DatasetRows"
              ("DatasetId", ((("Data"->>'tarih'))::timestamp))
            """,
            "filtre_donem / satir_ilk_sayfa",
            "Tarih hem sıralamanın hem dönem filtresinin dayanağı. Bu adayın KURULMASI " +
            "beklenmiyor: text→timestamp dönüşümü DateStyle ayarına bağlı olduğu için " +
            "PostgreSQL onu IMMUTABLE saymaz. Denenmesinin sebebi de bu — sınırın " +
            "nerede olduğunu tahmin değil hata mesajı söylemeli."),

        new("ix_olcum_data_gin",
            """
            CREATE INDEX ix_olcum_data_gin ON "DatasetRows"
              USING gin ("Data" jsonb_path_ops)
            """,
            "(bugünkü sorguların hiçbiri)",
            "Kolon adı bilmeden BÜTÜN jsonb'yi indeksleyen tek genel aday. Bugünkü SQL " +
            "bundan faydalanamaz, çünkü `@>` değil `->>` kullanıyor. Ölçüme yine de " +
            "giriyor: maliyeti (boyut, kurulum süresi, yazmaya etkisi) genel bir " +
            "çözümün bedelini gösteriyor.")
    ];

    // Trigram ve btree_gin uzantıları: ikisi de PostgreSQL'in standart dağıtımında gelir
    // ama açıkça etkinleştirilmeleri gerekir.
    private static readonly string[] Uzantilar =
    [
        "CREATE EXTENSION IF NOT EXISTS pg_trgm",
        "CREATE EXTENSION IF NOT EXISTS btree_gin"
    ];

    public static async Task<IReadOnlyList<IndeksAdayi>> KurAsync(NpgsqlConnection baglanti)
    {
        foreach (var sql in Uzantilar) await KomutAsync(baglanti, sql);

        var adaylar = Adaylar();

        foreach (var aday in adaylar)
        {
            var saat = Stopwatch.StartNew();

            try
            {
                await KomutAsync(baglanti, aday.Sql);
                aday.SaniyeKurulum = saat.Elapsed.TotalSeconds;
                aday.Boyut = await BoyutAsync(baglanti, aday.Ad);

                Console.WriteLine(
                    $"  + {aday.Ad,-22} {aday.SaniyeKurulum,6:N1} sn  {aday.Boyut}");
            }
            catch (PostgresException ex)
            {
                aday.Hata = ex.MessageText;
                Console.WriteLine($"  ! {aday.Ad,-22} KURULAMADI: {ex.MessageText}");
            }
        }

        // İndeksler kurulduktan sonra planlayıcının onları görebilmesi için istatistik
        // gerekir; ANALYZE atlanırsa ilk koşular indekssizmiş gibi ölçülür.
        await KomutAsync(baglanti, """ANALYZE "DatasetRows" """);

        return adaylar;
    }

    public static async Task DusurAsync(NpgsqlConnection baglanti)
    {
        foreach (var aday in Adaylar())
            await KomutAsync(baglanti, $"DROP INDEX IF EXISTS {aday.Ad}");

        await KomutAsync(baglanti, """ANALYZE "DatasetRows" """);
    }

    private static async Task<string> BoyutAsync(NpgsqlConnection baglanti, string ad)
    {
        await using var komut = baglanti.CreateCommand();
        komut.CommandText = $"SELECT pg_size_pretty(pg_relation_size('{ad}'))";
        return (await komut.ExecuteScalarAsync())?.ToString() ?? "?";
    }

    private static async Task KomutAsync(NpgsqlConnection baglanti, string sql)
    {
        await using var komut = baglanti.CreateCommand();
        komut.CommandText = sql;
        komut.CommandTimeout = 1800; // 2,2 milyon satırda GIN kurulumu uzun sürebilir
        await komut.ExecuteNonQueryAsync();
    }
}
