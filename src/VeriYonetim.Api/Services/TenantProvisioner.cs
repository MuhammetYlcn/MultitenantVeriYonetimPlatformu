using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Services;

public interface ITenantProvisioner
{
    string BuildSchemaName(string slug);
    Task CreateSchemaAsync(string schemaName);
    Task<int> SyncAllSchemasAsync();
}

public class TenantProvisioner : ITenantProvisioner
{
    // DTO'daki RegularExpression ile aynı whitelist. Servis, çağıranın doğrulama
    // yaptığına güvenmez (defense in depth) — buradan geçmeyen ad SQL'e asla gömülmez.
    private static readonly Regex SafeSlug = new("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);

    // Şema adı DB'den de gelebilir (SyncAllSchemasAsync); SQL'i kuran metod
    // girdisini kim üretmiş olursa olsun kendisi doğrular. 63 = PostgreSQL limiti.
    private static readonly Regex SafeSchema = new("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);

    private readonly AppDbContext _db;

    public TenantProvisioner(AppDbContext db)
    {
        _db = db;
    }

    public string BuildSchemaName(string slug)
    {
        if (!SafeSlug.IsMatch(slug) || slug.Length > 56)
            throw new ArgumentException($"Geçersiz slug formatı: '{slug}'", nameof(slug));

        return "tenant_" + slug.Replace('-', '_');
    }

    public async Task CreateSchemaAsync(string schemaName)
    {
        if (!SafeSchema.IsMatch(schemaName))
            throw new ArgumentException($"Geçersiz şema adı: '{schemaName}'", nameof(schemaName));

        // Tanımlayıcılar (şema/tablo adı) parametre olamaz; ad SQL metnine gömülür.
        // Güvenlik yukarıdaki whitelist'e dayanır, tırnaklama ek katmandır.
        // IF NOT EXISTS → idempotent: açılış senkronizasyonu güvenle tekrar çalışabilir.
#pragma warning disable EF1002 // parametreli sorgu burada mümkün değil, whitelist ile korunuyor
        await _db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\"");
#pragma warning restore EF1002
    }

    /// <summary>
    /// SchemaName'i kayıtlı olup fiziksel şeması eksik olan tenant'ları tamamlar.
    /// Uygulama açılışında çalışır; provisioning eklenmeden önce kaydolmuş
    /// tenant'lar (backfill) ve yarım kalmış açılışlar için güvenlik ağıdır.
    /// </summary>
    public async Task<int> SyncAllSchemasAsync()
    {
        var schemaNames = await _db.Tenants.Select(t => t.SchemaName).ToListAsync();

        foreach (var name in schemaNames)
            await CreateSchemaAsync(name);

        return schemaNames.Count;
    }
}
