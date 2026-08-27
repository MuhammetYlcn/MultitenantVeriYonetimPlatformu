using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace VeriYonetim.Api.Tests;

/// <summary>
/// Açılış sertleştirmesinin testleri.
///
/// Buradaki iki iddia da 26.08'de yazılan güvencelerin KAPSAMADIĞI hâllerdi ve kod
/// incelemesinde bulundu:
///
///   1. "Eksik/zayıf sır = açılmayan uygulama" deniyordu, ama yakalamak istediği asıl
///      senaryoyu — örnek dosyayı kopyalayıp doldurmamayı — kaçırıyordu: depodaki yer
///      tutucu 50 karakter olduğu için 32 baytlık eşiği rahatça aşıyordu. O durumda
///      imzalama anahtarı herkese açık depoda YAZILI BİR SABİT olur.
///   2. Kayıt ucu kimlik doğrulamasız, sınırsız ve kalıcı kaynak (firma + PostgreSQL
///      şeması) yaratıyordu.
///
/// Bu testler kendi fabrikalarını kuruyor: ikisi de ortak <see cref="ApiFactory"/>'nin
/// ayarlarını bilerek DEĞİŞTİRİYOR.
/// </summary>
public class StartupHardeningTests
{
    /// <summary>
    /// Verilen ayarlarla uygulamayı ayağa kaldırmayı dener. Testin kendi veritabanına
    /// bağlanması gerekiyor, yoksa hata sırdan değil bağlantıdan gelirdi.
    /// </summary>
    private sealed class YapilandirilmisFabrika : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _ayarlar;

        public YapilandirilmisFabrika(Dictionary<string, string?> ayarlar) =>
            _ayarlar = ayarlar;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                var cs = context.Configuration.GetConnectionString("DefaultConnection");
                var csb = new NpgsqlConnectionStringBuilder(cs) { Database = "veriyonetim_test" };

                var hepsi = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = csb.ConnectionString,
                    ["PlatformAdmin:Email"] = ApiFactory.PlatformAdminEmail,
                    ["PlatformAdmin:Password"] = ApiFactory.PlatformAdminPassword,
                    ["Hangfire:RunServer"] = "false",
                    ["Email:Host"] = "",
                    ["Security:Register:Enabled"] = "false"
                };

                // Testin geçersiz kılmaları EN SON uygulanıyor: AddInMemoryCollection,
                // UseSetting'ten daha yüksek önceliğe sahip, dolayısıyla buraya da
                // konmazsa yukarıdaki varsayılanlar testin değerini ezerdi.
                foreach (var (anahtar, deger) in _ayarlar) hepsi[anahtar] = deger;

                config.AddInMemoryCollection(hepsi);
            });

            // Testin DEĞİŞTİRDİĞİ ayarlar UseSetting ile veriliyor, AddInMemoryCollection
            // ile değil. Sebebi, kod incelemesi sırasında hız sınırında da karşımıza çıkan
            // sıralama: Program.cs gövdesi `builder.Build()`'den ÖNCE koşuyor, yani açılış
            // sır denetimleri `ConfigureAppConfiguration` uygulanmadan okunuyor.
            // `UseSetting` host yapılandırmasına yazdığı için o okumada zaten görünür.
            foreach (var (anahtar, deger) in _ayarlar)
                builder.UseSetting(anahtar, deger);
        }
    }

    // ---- Yer tutucu sırlar ----

    [Fact(DisplayName = "Örnek dosyadaki yer tutucu Jwt:Key ile uygulama AÇILMIYOR")]
    public void PlaceholderJwtKey_StopsStartup()
    {
        // .env.example'daki değerin birebir kendisi. 50 karakter olduğu için eski uzunluk
        // denetiminden geçiyordu — asıl tehlike de buydu: doldurulmuş GÖRÜNEN ayar.
        using var fabrika = new YapilandirilmisFabrika(new()
        {
            ["Jwt:Key"] = "BURAYA-EN-AZ-32-KARAKTERLIK-RASTGELE-GIZLI-ANAHTAR"
        });

        var hata = Assert.Throws<InvalidOperationException>(() => fabrika.CreateClient());

        Assert.Contains("yer tutucu", hata.Message);
    }

    [Fact(DisplayName = "Yer tutucu şifre taşıyan bağlantı dizesiyle uygulama AÇILMIYOR")]
    public void PlaceholderConnectionString_StopsStartup()
    {
        // Bağlantı dizesinde yer tutucu BAŞTA değil ortada duruyor; denetim bu yüzden
        // StartsWith değil Contains ile yapılıyor.
        using var fabrika = new YapilandirilmisFabrika(new()
        {
            ["ConnectionStrings:DefaultConnection"] =
                "Host=localhost;Database=veriyonetim;Username=veriyonetim;" +
                "Password=BURAYA-YEREL-DB-SIFRESI"
        });

        var hata = Assert.Throws<InvalidOperationException>(() => fabrika.CreateClient());

        Assert.Contains("yer tutucu", hata.Message);
    }

    [Fact(DisplayName = "32 karakterden kısa Jwt:Key ile uygulama AÇILMIYOR")]
    public void ShortJwtKey_StopsStartup()
    {
        using var fabrika = new YapilandirilmisFabrika(new()
        {
            ["Jwt:Key"] = "kisa-anahtar"
        });

        Assert.Throws<InvalidOperationException>(() => fabrika.CreateClient());
    }

    [Fact(DisplayName = "Jwt:Issuer eksikse uygulama AÇILMIYOR " +
                        "(açılsaydı her giriş 401 üretirdi)")]
    public void MissingIssuer_StopsStartup()
    {
        using var fabrika = new YapilandirilmisFabrika(new()
        {
            ["Jwt:Issuer"] = ""
        });

        var hata = Assert.Throws<InvalidOperationException>(() => fabrika.CreateClient());

        Assert.Contains("Jwt:Issuer", hata.Message);
    }

    // ---- Kayıt ucundaki hız sınırı ----

    [Fact(DisplayName = "Kayıt ucu sınırlı: kotayı aşan istek 429 alıyor")]
    public async Task RegisterEndpoint_IsRateLimited()
    {
        const int kota = 3;

        using var fabrika = new YapilandirilmisFabrika(new()
        {
            ["Security:Register:Enabled"] = "true",
            ["Security:Register:MaxPerWindow"] = kota.ToString(),
            ["Security:Register:WindowMinutes"] = "15"
        });

        using var client = fabrika.CreateClient();

        for (var i = 0; i < kota; i++)
        {
            var izinli = await client.PostAsJsonAsync("/api/auth/register",
                new { tenantName = $"kota{i}", email = $"kota{i}@sinir.com",
                      password = "Sifre123!" });

            Assert.NotEqual(HttpStatusCode.TooManyRequests, izinli.StatusCode);
        }

        // Kota doldu: bir sonraki istek işlenmeden eleniyor. Elenen şey yalnız hesap
        // sayımı değil, kalıcı bir PostgreSQL şeması açma denemesi.
        var asan = await client.PostAsJsonAsync("/api/auth/register",
            new { tenantName = "asan", email = "asan@sinir.com", password = "Sifre123!" });

        Assert.Equal(HttpStatusCode.TooManyRequests, asan.StatusCode);
    }
}
