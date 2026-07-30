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
                ["PlatformAdmin:Password"] = PlatformAdminPassword
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
                           "PlatformAdmins", "PlatformAuditLogs" CASCADE
            """);

        var platformAuth = scope.ServiceProvider.GetRequiredService<IPlatformAuthService>();
        await platformAuth.EnsureSeedAdminAsync();
    }
}
