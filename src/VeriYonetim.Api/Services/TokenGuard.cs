using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using VeriYonetim.Api.Data;

namespace VeriYonetim.Api.Services;

/// <summary>
/// Elde duran bir access token'ın HÂLÂ geçerli olup olmadığını denetler.
///
/// Kapattığı açık: JWT kendi kendini doğrular, yani sunucu onu üretirken bildiklerini
/// taşır ve süresi dolana kadar hiçbir şey onu geri alamaz. Bunun bedeli üç yerde
/// somuttu ve kod incelemesinde bunlar "en geç 15 dakika" olarak kayıtlıydı:
///
///   • Askıya alınan firmanın kullanıcısı okumaya devam ediyordu. Giriş, oturum
///     yenileme, davet ve izleyici taraması kapanıyordu ama ELDEKİ token çalışıyordu;
///     yani "askıya aldım" demek "en geç 15 dakika sonra kapanacak" demekti.
///   • Admin'den Viewer'a düşürülen kullanıcı, token'ındaki eski rol claim'iyle yazmaya
///     devam ediyordu. Yetki düşürmenin gecikmeli olması, yükseltmenin gecikmeli
///     olmasından çok daha ciddi.
///   • Silinen kullanıcının token'ı da ömrü boyunca geçerliydi.
///
/// NEDEN ÖNBELLEK VAR. Doğru cevap her istekte veritabanına bakmak olurdu, ama o
/// zaman sistemdeki EN SIK sorgu bu denetim olurdu — kimlik doğrulaması her uçta
/// çalışıyor. Snapshot kısa ömürlü önbellekte tutuluyor:
///
///   • Normal yol: bellek içi sözlük okuması, veritabanına hiç gidilmiyor.
///   • Değişiklik olduğunda: askı ve rol değişikliği ilgili girdiyi AÇIKÇA düşürüyor,
///     yani etki ANINDA. Gecikme yalnız açık düşürmenin ulaşamadığı hâller için var.
///   • En kötü hâl artık token ömrü (15 dk) değil, önbellek ömrü (30 sn).
///
/// BİLİNEN KISIT, gizlenmiyor: önbellek SÜREÇ İÇİNDE. Tek örnekli teslimde (bugünkü
/// docker-compose) açık düşürme her zaman doğru örneğe ulaşır. Uygulama birden çok
/// örnekle ölçeklenirse diğer örneklerin önbelleği kendi süresi dolana kadar eski
/// cevabı verir — yani etki "anında" değil "en geç 30 saniye" olur. Bunu çözmek
/// paylaşılan bir önbellek (Redis) ya da her istekte veritabanı demek; ikisi de bu
/// projenin teslim biçiminin dışında.
/// </summary>
public interface ITokenGuard
{
    /// <summary>Token reddedilmeliyse sebebi, geçerliyse null döner.</summary>
    Task<string?> RejectReasonAsync(Guid userId, Guid tenantId, string? tokenRole,
        CancellationToken ct = default);

    /// <summary>Tek bir kullanıcının snapshot'ını düşürür (rol değişikliği, silme).</summary>
    void Invalidate(Guid userId);

    /// <summary>Bir firmanın TÜM kullanıcılarının snapshot'ını düşürür (askı).</summary>
    void InvalidateTenant(Guid tenantId);
}

public class TokenGuard : ITokenGuard
{
    /// <summary>
    /// Snapshot ömrü. Kısa tutuldu, çünkü bu sürenin tamamı "yetkisi alınmış bir
    /// token'ın hâlâ çalıştığı en kötü hâl". Sıfıra indirilemez: sıfır, her isteğe bir
    /// veritabanı sorgusu demek olurdu ve kimlik doğrulaması her uçta çalışıyor.
    /// </summary>
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Firma başına iptal kaynağı — bir firmanın bütün kullanıcılarını TEK hamlede
    /// düşürmek için. Kullanıcı girdileri bu kaynağın değişim jetonuna bağlanıyor;
    /// kaynak iptal edilince o firmaya ait bütün girdiler aynı anda geçersiz oluyor.
    /// Kullanıcıları tek tek gezmek gerekmiyor.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> TenantTokens =
        new();

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public TokenGuard(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private sealed record Snapshot(bool Exists, bool TenantActive, string Role);

    public async Task<string?> RejectReasonAsync(Guid userId, Guid tenantId, string? tokenRole,
        CancellationToken ct = default)
    {
        var snapshot = await _cache.GetOrCreateAsync(Key(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = SnapshotLifetime;
            entry.AddExpirationToken(new CancellationChangeToken(TenantSource(tenantId).Token));

            // Query filter BİLEREK atlanıyor: bu denetim kimlik doğrulamasının parçası,
            // yani tenant bağlamı henüz kurulmadan çalışıyor. Aranan kimlik token'dan
            // geliyor ve firma eşleşmesi aşağıda AYRICA doğrulanıyor.
            var row = await _db.Users
                .IgnoreQueryFilters()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Role, u.TenantId, Active = u.Tenant.IsActive })
                .FirstOrDefaultAsync(ct);

            // Kullanıcı yok ya da token'daki firma artık onun firması değil.
            if (row is null || row.TenantId != tenantId)
                return new Snapshot(false, false, string.Empty);

            return new Snapshot(true, row.Active, row.Role);
        });

        if (snapshot is null || !snapshot.Exists)
            return "Oturum artık geçerli değil.";

        if (!snapshot.TenantActive)
            return "Firmanız askıya alınmış.";

        // ROL DEĞİŞİKLİĞİ. Token'daki rol claim'i yetkilendirmeyi belirliyor; rol
        // düşürüldüğünde eski claim'in çalışmaya devam etmesi, yetki almanın gecikmesi
        // demek. Eşleşmiyorsa token reddediliyor ve kullanıcı yenilemeye zorlanıyor —
        // yenileme yeni rolü taşıyan bir token üretir.
        if (!string.Equals(snapshot.Role, tokenRole, StringComparison.Ordinal))
            return "Yetkileriniz değişmiş; yeniden giriş yapın.";

        return null;
    }

    public void Invalidate(Guid userId) => _cache.Remove(Key(userId));

    public void InvalidateTenant(Guid tenantId)
    {
        // Kaynağı sözlükten ÇIKARIP iptal ediyoruz: iptal edilmiş bir kaynak yeniden
        // kullanılamaz, sonraki girdiler taze bir kaynağa bağlanmalı.
        if (TenantTokens.TryRemove(tenantId, out var source))
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private static CancellationTokenSource TenantSource(Guid tenantId) =>
        TenantTokens.GetOrAdd(tenantId, _ => new CancellationTokenSource());

    private static string Key(Guid userId) => $"tokenguard:{userId}";
}
