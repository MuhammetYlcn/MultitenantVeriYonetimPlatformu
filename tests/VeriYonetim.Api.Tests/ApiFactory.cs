using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

/// <summary>
/// Uygulamayı bellek içi test sunucusunda ayağa kaldırır (Spring @SpringBootTest
/// karşılığı). Tek fark: bağlantı, aynı PostgreSQL sunucusundaki ayrı
/// veriyonetim_test veritabanına yönlendirilir — gerçek veriye test bulaşmaz.
/// Açılıştaki MigrateAsync taze test DB'sini kendisi kurar.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Testlerdeki platform yöneticisi kimliği. Makinedeki appsettings.Development.json'a
    /// bağlı kalmamak için burada sabitlenir — testler her ortamda aynı davranır.
    /// </summary>
    public const string PlatformAdminEmail = "platform-test@veriyonetim.local";
    public const string PlatformAdminPassword = "PlatformSifre123!";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var cs = context.Configuration.GetConnectionString("DefaultConnection");
            var csb = new NpgsqlConnectionStringBuilder(cs) { Database = "veriyonetim_test" };

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = csb.ConnectionString,
                ["PlatformAdmin:Email"] = PlatformAdminEmail,
                ["PlatformAdmin:Password"] = PlatformAdminPassword,

                // Arka plan işçisi testlerde ÇALIŞMAZ. Kuyruğa alma yine gerçekleşir, ama
                // işi kimin ne zaman çalıştıracağına test karar verir (çalıştırıcı elle
                // tetiklenir). Açık bırakılsaydı testler zamanlamaya bağımlı hâle gelir,
                // üstelik gerçek görsel modeli çağırmaya kalkarlardı.
                ["Hangfire:RunServer"] = "false",

                // E-posta testlerde KAPALI. Geliştirme ayarında açık duruyor (yerel
                // Mailpit'e gidiyor), ama testin sonucu makinede bir posta sunucusunun
                // ayakta olup olmamasına bağlı olmamalı. Gönderimi sınayan testler
                // IEmailSender'ın yerine sahtesini koyup mesajın kendisine bakıyor.
                ["Email:Host"] = ""
            });
        });
    }

    /// <summary>
    /// Her testin temiz veriyle başlaması için tabloları boşaltır. Platform tabloları
    /// da temizlenir (denetim kayıtları testler arasında sızmasın), ardından platform
    /// yöneticisi yeniden tohumlanır — aksi hâlde tohumlama yalnız açılışta çalıştığı
    /// için ilk testten sonra platform girişi yapılamazdı.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE "Datasets", "RefreshTokens", "AccountTokens", "Users", "Tenants",
                           "PlatformAdmins", "PlatformAuditLogs", "DocumentJobs",
                           "DatasetWatches" CASCADE
            """);

        // Kolon indeksleri tabloya kurulur, satırlara değil: TRUNCATE onları düşürmez.
        // Temizlenmezlerse bir testin kurduğu indeks bir sonrakinde duruyor olur ve
        // "indeks var mı" diye soran testler sessizce yanlış cevap alır.
        await db.Database.ExecuteSqlRawAsync(
            """
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN SELECT indexname FROM pg_indexes
                         WHERE tablename = 'DatasetRows' AND indexname LIKE 'ix\_rows\_%'
                LOOP
                    EXECUTE format('DROP INDEX IF EXISTS %I', r.indexname);
                END LOOP;
            END $$;
            """);

        var platformAuth = scope.ServiceProvider.GetRequiredService<IPlatformAuthService>();
        await platformAuth.EnsureSeedAdminAsync();
    }
}
