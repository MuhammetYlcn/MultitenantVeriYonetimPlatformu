using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace VeriYonetim.Olcum;

// Ölçüm verisini üretir ve veritabanına basar.
//
// Satırlar TEK TEK değil, PostgreSQL'in ikili COPY akışıyla yazılır. Uygulamanın bugünkü
// içe aktarma yolu satırları tek tek ekliyor (DatasetsController'da not düşülü); 1 milyon
// satırda bu yol saatler sürer ve ölçümü imkânsız kılardı. Buradaki yazıcı önce ölçümün
// aracı, sonra üretim yolunun iyileştirmesi olacak.
internal static class Tohumlama
{
    public static async Task KurAsync()
    {
        var saat = Stopwatch.StartNew();

        await VeritabaniniOlusturAsync();

        Console.WriteLine($"Şema kuruluyor ({Ortam.VeritabaniAdi})…");
        await using (var db = Ortam.DbAc())
            await db.Database.MigrateAsync();

        await using var baglanti = new NpgsqlConnection(Ortam.BaglantiDizesi());
        await baglanti.OpenAsync();

        await EskisiniSilAsync(baglanti);

        // Ölçülen firma: üç ölçek + JOIN'in karşı tarafı olan müşteri seti.
        var olculen = await FirmaKurAsync(baglanti, Ortam.OlculenSlug, "Ölçüm A", Ortam.KullaniciEposta);

        foreach (var (ad, _, satir) in Ortam.Olcekler)
            await SatisSetiBasAsync(baglanti, olculen, ad, satir, tohum: satir);

        await MusteriSetiBasAsync(baglanti, olculen);

        // Gürültü firması: aynı boyutta veri, ölçüme HİÇ girmiyor. Varlık sebebi tek:
        // ölçülen sorgunun tablodaki tek veri olmadığını göstermek.
        var gurultu = await FirmaKurAsync(baglanti, Ortam.GurultuSlug, "Ölçüm B", "olcum-b@olcum.local");

        foreach (var (ad, _, satir) in Ortam.Olcekler)
            await SatisSetiBasAsync(baglanti, gurultu, ad, satir, tohum: satir + 1);

        // Yazma ölçümünün kullandığı BOŞ setler de burada kuruluyor. Ölçüm sırasında
        // kurulsalardı `seed` sonrası durum ile `measure` sırasındaki durum farklı olur,
        // yani iki koşu farklı tabloları ölçerdi.
        await YazmaSetleriKurAsync(baglanti, olculen);

        // İstatistikler tazelenmeden ölçüm anlamsız: planlayıcı satır sayısını bilmezse
        // yanlış plan seçer ve ölçtüğümüz şey veritabanının kararı değil, tesadüf olur.
        Console.WriteLine("ANALYZE…");
        await KomutAsync(baglanti, "ANALYZE \"DatasetRows\"");

        var toplam = await TekDegerAsync(baglanti, "SELECT COUNT(*) FROM \"DatasetRows\"");
        Console.WriteLine($"Bitti: tabloda {toplam:N0} satır, {saat.Elapsed.TotalSeconds:N0} sn.");
    }

    public static async Task TemizleAsync()
    {
        await using var yonetim = new NpgsqlConnection(Ortam.BaglantiDizesi("postgres"));
        await yonetim.OpenAsync();

        // Açık bağlantı varken DROP DATABASE çalışmaz; önce oturumlar düşürülür.
        await KomutAsync(yonetim,
            $"""
             SELECT pg_terminate_backend(pid) FROM pg_stat_activity
             WHERE datname = '{Ortam.VeritabaniAdi}' AND pid <> pg_backend_pid()
             """);

        await KomutAsync(yonetim, $"DROP DATABASE IF EXISTS \"{Ortam.VeritabaniAdi}\"");
        Console.WriteLine($"{Ortam.VeritabaniAdi} silindi.");
    }

    private static async Task VeritabaniniOlusturAsync()
    {
        await using var yonetim = new NpgsqlConnection(Ortam.BaglantiDizesi("postgres"));
        await yonetim.OpenAsync();

        var var_mi = await TekDegerAsync(yonetim,
            $"SELECT COUNT(*) FROM pg_database WHERE datname = '{Ortam.VeritabaniAdi}'");

        if (var_mi == 0)
        {
            await KomutAsync(yonetim, $"CREATE DATABASE \"{Ortam.VeritabaniAdi}\"");
            Console.WriteLine($"{Ortam.VeritabaniAdi} oluşturuldu.");
        }
    }

    // Firma silinince veri setleri ve satırları cascade ile gider — AMA ilişkiler gitmez:
    // DatasetRelations'ın "To" ucu bilinçli olarak RESTRICT (iki uçta da cascade olsaydı
    // PostgreSQL "multiple cascade paths" derdi, bkz. AppDbContext). O yüzden ilişkiler
    // önce elle düşürülüyor; yoksa ikinci `seed` koşusu yabancı anahtar hatasıyla patlar.
    private static async Task EskisiniSilAsync(NpgsqlConnection baglanti)
    {
        await KomutAsync(baglanti,
            """
            DELETE FROM "DatasetRelations" r
            USING "Datasets" d, "Tenants" t
            WHERE (r."FromDatasetId" = d."Id" OR r."ToDatasetId" = d."Id")
              AND d."TenantId" = t."Id"
              AND t."Slug" LIKE 'olcum-%'
            """);

        await KomutAsync(baglanti, "DELETE FROM \"Tenants\" WHERE \"Slug\" LIKE 'olcum-%'");
    }

    // Yazma ölçümünün üç boş seti: ikisi ölçülen firmada (komşuları var), biri komşusuz
    // bir firmada. Üçü de boş — ölçüm kendi satırlarını kendisi yazacak.
    private static async Task YazmaSetleriKurAsync(NpgsqlConnection baglanti, Guid olculen)
    {
        await VeriSetiKurAsync(baglanti, olculen, "yazma_uc", Ortam.SatisSemasi, 0);
        await VeriSetiKurAsync(baglanti, olculen, "yazma_copy", Ortam.SatisSemasi, 0);

        var yalniz = await FirmaKurAsync(
            baglanti, Ortam.YalnizSlug, "Ölçüm C", Ortam.YalnizEposta);

        await VeriSetiKurAsync(baglanti, yalniz, "yazma_yalniz", Ortam.SatisSemasi, 0);
    }

    private static async Task<Guid> FirmaKurAsync(
        NpgsqlConnection baglanti, string slug, string ad, string eposta)
    {
        var firmaId = Guid.NewGuid();
        var simdi = DateTime.UtcNow;

        await using (var komut = baglanti.CreateCommand())
        {
            komut.CommandText =
                """
                INSERT INTO "Tenants" ("Id", "Name", "Slug", "SchemaName", "CreatedAt", "IsActive")
                VALUES (@id, @ad, @slug, @sema, @simdi, true)
                """;
            komut.Parameters.AddWithValue("id", firmaId);
            komut.Parameters.AddWithValue("ad", ad);
            komut.Parameters.AddWithValue("slug", slug);
            komut.Parameters.AddWithValue("sema", "tenant_" + slug.Replace('-', '_'));
            komut.Parameters.AddWithValue("simdi", simdi);
            await komut.ExecuteNonQueryAsync();
        }

        await using (var komut = baglanti.CreateCommand())
        {
            komut.CommandText =
                """
                INSERT INTO "Users" ("Id", "Email", "PasswordHash", "Role", "CreatedAt", "TenantId")
                VALUES (@id, @eposta, @ozet, 'Admin', @simdi, @firma)
                """;
            komut.Parameters.AddWithValue("id", Guid.NewGuid());
            komut.Parameters.AddWithValue("eposta", eposta);
            komut.Parameters.AddWithValue("ozet", BCrypt.Net.BCrypt.HashPassword(Ortam.Sifre));
            komut.Parameters.AddWithValue("simdi", simdi);
            komut.Parameters.AddWithValue("firma", firmaId);
            await komut.ExecuteNonQueryAsync();
        }

        return firmaId;
    }

    private static async Task<Guid> VeriSetiKurAsync(NpgsqlConnection baglanti, Guid firmaId,
        string ad, (string Ad, string Tip)[] sema, int satirSayisi)
    {
        var setId = Guid.NewGuid();

        await using (var komut = baglanti.CreateCommand())
        {
            komut.CommandText =
                """
                INSERT INTO "Datasets" ("Id", "Name", "Description", "RowCount", "CreatedAt", "TenantId")
                VALUES (@id, @ad, @aciklama, @sayi, @simdi, @firma)
                """;
            komut.Parameters.AddWithValue("id", setId);
            komut.Parameters.AddWithValue("ad", ad);
            komut.Parameters.AddWithValue("aciklama", "Ölçüm için üretilmiş veri.");
            komut.Parameters.AddWithValue("sayi", satirSayisi);
            komut.Parameters.AddWithValue("simdi", DateTime.UtcNow);
            komut.Parameters.AddWithValue("firma", firmaId);
            await komut.ExecuteNonQueryAsync();
        }

        for (var i = 0; i < sema.Length; i++)
        {
            await using var komut = baglanti.CreateCommand();
            komut.CommandText =
                """
                INSERT INTO "DatasetColumns" ("Id", "Name", "Type", "Ordinal", "DatasetId")
                VALUES (@id, @ad, @tip, @sira, @set)
                """;
            komut.Parameters.AddWithValue("id", Guid.NewGuid());
            komut.Parameters.AddWithValue("ad", sema[i].Ad);
            komut.Parameters.AddWithValue("tip", sema[i].Tip);
            komut.Parameters.AddWithValue("sira", i);
            komut.Parameters.AddWithValue("set", setId);
            await komut.ExecuteNonQueryAsync();
        }

        return setId;
    }

    private static async Task SatisSetiBasAsync(NpgsqlConnection baglanti, Guid firmaId,
        string ad, int satirSayisi, int tohum)
    {
        var setId = await VeriSetiKurAsync(baglanti, firmaId, ad, Ortam.SatisSemasi, satirSayisi);
        var uretici = new Uretici(tohum);

        await BasAsync(baglanti, setId, satirSayisi, ad, uretici.Satis);
    }

    private static async Task MusteriSetiBasAsync(NpgsqlConnection baglanti, Guid firmaId)
    {
        var setId = await VeriSetiKurAsync(
            baglanti, firmaId, Ortam.MusteriSeti, Ortam.MusteriSemasi, Ortam.MusteriSayisi);

        var uretici = new Uretici(7);
        await BasAsync(baglanti, setId, Ortam.MusteriSayisi, Ortam.MusteriSeti, uretici.Musteri);
    }

    // İkili COPY akışı. Satırlar tek tek üretilip akışa yazılır: 1 milyon satır belleğe
    // toplanmaz.
    private static async Task BasAsync(NpgsqlConnection baglanti, Guid setId, int satirSayisi,
        string ad, Func<int, Dictionary<string, object?>> uret)
    {
        var saat = Stopwatch.StartNew();

        // AppDbContext'teki ValueConverter ile AYNI seçenekler: ölçülen JSON metni,
        // uygulamanın yazdığı JSON metniyle birebir aynı olmalı (tarih biçimi, ondalık
        // ayracı). Farklı olsaydı ölçüm başka bir veriyi ölçerdi.
        var secenekler = new JsonSerializerOptions();

        await using var yazici = await baglanti.BeginBinaryImportAsync(
            "COPY \"DatasetRows\" (\"Id\", \"Data\", \"DatasetId\") FROM STDIN (FORMAT BINARY)");

        for (var i = 0; i < satirSayisi; i++)
        {
            await yazici.StartRowAsync();
            await yazici.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
            await yazici.WriteAsync(JsonSerializer.Serialize(uret(i), secenekler), NpgsqlDbType.Jsonb);
            await yazici.WriteAsync(setId, NpgsqlDbType.Uuid);
        }

        await yazici.CompleteAsync();

        var saniye = saat.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"  {ad,-12} {satirSayisi,9:N0} satır  {saniye,6:N1} sn  ({satirSayisi / Math.Max(saniye, 0.001):N0} satır/sn)");
    }

    private static async Task KomutAsync(NpgsqlConnection baglanti, string sql)
    {
        await using var komut = baglanti.CreateCommand();
        komut.CommandText = sql;
        komut.CommandTimeout = 600;
        await komut.ExecuteNonQueryAsync();
    }

    private static async Task<long> TekDegerAsync(NpgsqlConnection baglanti, string sql)
    {
        await using var komut = baglanti.CreateCommand();
        komut.CommandText = sql;
        return Convert.ToInt64(await komut.ExecuteScalarAsync());
    }
}
