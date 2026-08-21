using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Olcum;

// Ölçümün üzerinde koştuğu ortam: hangi veritabanı, hangi firma, hangi veri setleri.
//
// Ölçüm AYRI BİR VERİTABANINDA koşar (varsayılan: veriyonetim_olcum). Geliştirme
// veritabanına 2 milyon satır basmak hem oradaki elle kurulmuş örnekleri kullanılamaz
// hale getirir hem de ölçümü tekrarlanamaz kılar: bir sonraki koşuda tablo bambaşka
// olur. Ayrı veritabanı atılabilir bir şeydir, `temizle` ile silinir.
internal static class Ortam
{
    // Ölçüm veritabanının adı. Geliştirme veritabanıyla aynı sunucuda, ayrı DB olarak durur.
    public const string VeritabaniAdi = "veriyonetim_olcum";

    // Ölçülen firma ve gürültü firması. İkincisinin varlığı ölçümün ASIL sorusudur:
    // "başka firmaların satırları aynı tabloda dururken tek firmanın sorgusu ne kadar sürer".
    public const string OlculenSlug = "olcum-a";
    public const string GurultuSlug = "olcum-b";

    public const string KullaniciEposta = "olcum-a@olcum.local";

    // Üçüncü firma yalnız yazma ölçümü için: içinde tek bir boş veri seti var, yani
    // içe aktarmadan sonra koşan ilişki algılamasının karşılaştıracağı komşusu yok.
    public const string YalnizSlug = "olcum-c";
    public const string YalnizEposta = "olcum-c@olcum.local";

    // Atılabilir ölçüm veritabanındaki tek kullanıcının şifresi. Gerçek bir sır değil:
    // bu veritabanı araç tarafından kurulur, `temizle` ile silinir ve içinde üretilmiş
    // rastgele veriden başka bir şey yoktur. Yine de ortam değişkeniyle değiştirilebilir.
    public static string Sifre => Environment.GetEnvironmentVariable("OLCUM_SIFRE") ?? "Olcum!2026";

    // Ölçeklerin her biri AYRI bir veri seti. Üçü de aynı tabloda durur — böylece 10k'lık
    // set ölçülürken tabloda 2 milyon satır bulunur ve "veri büyüyünce küçük set de
    // yavaşlar mı" sorusu doğrudan ölçülmüş olur.
    public static readonly (string Ad, string Etiket, int Satir)[] Olcekler =
    {
        ("satis_10k", "10k", 10_000),
        ("satis_100k", "100k", 100_000),
        ("satis_1m", "1M", 1_000_000)
    };

    // JOIN'in karşı tarafı. Müşteri sayısı satış sayısıyla birlikte büyümez (gerçekte de
    // öyle): 5.000 müşteriye milyonlarca satış düşer.
    public const string MusteriSeti = "musteriler";
    public const int MusteriSayisi = 5_000;

    // Satış setinin şeması (kolon adı → tip), kolon sırasıyla.
    public static readonly (string Ad, string Tip)[] SatisSemasi =
    {
        ("fatura_no", "text"),
        ("tarih", "date"),
        ("sehir", "text"),
        ("kategori", "text"),
        ("musteri_kodu", "text"),
        ("urun", "text"),
        ("miktar", "number"),
        ("birim_fiyat", "number"),
        ("tutar", "number")
    };

    public static readonly (string Ad, string Tip)[] MusteriSemasi =
    {
        ("musteri_kodu", "text"),
        ("unvan", "text"),
        ("sehir", "text"),
        ("segment", "text")
    };

    public static Dictionary<string, string> Sema((string Ad, string Tip)[] kolonlar) =>
        kolonlar.ToDictionary(k => k.Ad, k => k.Tip);

    // Bağlantı dizesi: ortam değişkeni > appsettings.Development.json'daki dizenin
    // veritabanı adı değiştirilmiş hâli. İkinci yol bilinçli — araca ayrıca bir parola
    // yazılmasın diye; sır yalnız zaten var olan ayar dosyasında durur.
    public static string BaglantiDizesi(string? veritabani = null)
    {
        var ortamdan = Environment.GetEnvironmentVariable("OLCUM_BAGLANTI");
        var temel = ortamdan ?? GelistirmeDizesi();

        var kurucu = new NpgsqlConnectionStringBuilder(temel)
        {
            Database = veritabani ?? VeritabaniAdi,
            // 1M satırlık COPY ve indekssiz agregasyon uzun sürebilir; varsayılan 30 sn
            // ölçümün kendisini kesecek kadar kısa.
            CommandTimeout = 600
        };

        return kurucu.ConnectionString;
    }

    private static string GelistirmeDizesi()
    {
        var kok = DepoKoku();
        var yol = Path.Combine(kok, "src", "VeriYonetim.Api", "appsettings.Development.json");

        if (!File.Exists(yol))
            throw new InvalidOperationException(
                $"Bağlantı dizesi bulunamadı. {yol} yok; OLCUM_BAGLANTI ortam değişkenini kurun.");

        using var belge = JsonDocument.Parse(File.ReadAllText(yol));
        var dize = belge.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("DefaultConnection")
            .GetString();

        return dize ?? throw new InvalidOperationException("DefaultConnection boş.");
    }

    // Çıktı dosyaları depo kökündeki raporlar/ altına yazılsın diye: araç hangi dizinden
    // çalıştırılırsa çalıştırılsın .sln dosyasını yukarı doğru arayarak kökü bulur.
    public static string DepoKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);

        while (dizin is not null)
        {
            if (File.Exists(Path.Combine(dizin.FullName, "VeriYonetim.sln")))
                return dizin.FullName;

            dizin = dizin.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    // Migration için DbContext. Araç sorguları ham Npgsql ile koşturur (ölçtüğü şey EF
    // değil, veritabanı); DbContext'e yalnız şemayı kurmak için ihtiyaç var.
    public static AppDbContext DbAc()
    {
        var secenekler = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(BaglantiDizesi())
            .Options;

        return new AppDbContext(secenekler, new SabitTenant());
    }

    // Migration'ın tenant bağlamına ihtiyacı yok; query filter'lar bu araçta kullanılmıyor.
    private sealed class SabitTenant : ITenantContext
    {
        public Guid? TenantId => null;
    }
}
