using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Tests;

/// <summary>
/// Uygulamayı bellek içi test sunucusunda ayağa kaldırır (Spring @SpringBootTest
/// karşılığı). Tek fark: bağlantı, aynı PostgreSQL sunucusundaki ayrı
/// veriyonetim_test veritabanına yönlendirilir — gerçek veriye test bulaşmaz.
/// Açılıştaki MigrateAsync taze test DB'sini kendisi kurar.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var cs = context.Configuration.GetConnectionString("DefaultConnection");
            var csb = new NpgsqlConnectionStringBuilder(cs) { Database = "veriyonetim_test" };

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = csb.ConnectionString
            });
        });
    }

    /// <summary>Her testin temiz veriyle başlaması için tabloları boşaltır.</summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE "RefreshTokens", "Users", "Tenants" CASCADE""");
    }
}
