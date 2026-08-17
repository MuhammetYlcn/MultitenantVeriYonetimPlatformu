using System.Security.Claims;

namespace VeriYonetim.Api.Services;

/// <summary>
/// Aktif firmanın kimliği. AppDbContext'teki bütün global query filter'lar buradan okur,
/// yani izolasyonun tamamı bu tek değere dayanır.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

/// <summary>
/// Tenant'ı ELLE kurma yetkisi — yalnızca arka plan işleri için.
///
/// Neden ayrı bir arayüz: okuma sözleşmesi (ITenantContext) her yere enjekte ediliyor.
/// Yazma yeteneği de aynı arayüzde dursaydı, herhangi bir controller çalışma anında
/// firmayı değiştirebilir hâle gelirdi. Bu arayüz yalnız iş çalıştırıcısına verilir.
/// </summary>
public interface ITenantContextSetter
{
    void SetForBackgroundWork(Guid tenantId);
}

/// <summary>
/// Tenant kimliğini önce elle kurulmuş değerden, yoksa oturum token'ından okur.
///
/// İki kaynağın olmasının sebebi: istek yolunda kimlik JWT'den gelir, ama arka plan
/// işinde (Hangfire) HTTP isteği YOKTUR. HttpContext null olduğunda TenantId de null
/// kalır ve o hâlde her query filter "TenantId == null" hâline döner — yani iş hiçbir
/// veriyi göremez. Bu, bilinçli olarak güvenli taraftır: bağlam kurulmayı unutulursa
/// sistem yanlış firmanın verisini işlemek yerine hiçbir şey bulamaz.
/// </summary>
public class TenantContext : ITenantContext, ITenantContextSetter
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private Guid? _backgroundTenantId;

    public TenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? TenantId
    {
        get
        {
            // Elle kurulmuş değer önce gelir; arka planda HttpContext zaten yoktur.
            if (_backgroundTenantId is { } backgroundTenantId) return backgroundTenantId;

            var value = _httpContextAccessor.HttpContext?
                .User.FindFirstValue("tenant_id");

            return Guid.TryParse(value, out var tenantId) ? tenantId : null;
        }
    }

    /// <summary>
    /// Arka plan işi kendi bağlamını kurar. Servis SCOPED olduğundan bu değer yalnız o
    /// işin kendi kapsamını etkiler, başka bir işi ya da isteği etkilemez.
    ///
    /// İki koruma var ve ikisi de veri sızıntısına karşı:
    ///   1. İstek yolunda çağrılamaz — bir controller çalışırken firma değiştirilemesin.
    ///   2. İkinci kez çağrılamaz — tek bir kapsam iki firmaya birden hizmet edemez.
    /// Bu yüzden ihlal sessizce yok sayılmıyor, istisna fırlatılıyor: yanlış firmanın
    /// verisiyle devam etmektense iş düşsün.
    /// </summary>
    public void SetForBackgroundWork(Guid tenantId)
    {
        if (_httpContextAccessor.HttpContext is not null)
            throw new InvalidOperationException(
                "Tenant bağlamı istek yolunda elle kurulamaz; kimlik token'dan gelir.");

        if (_backgroundTenantId is not null)
            throw new InvalidOperationException(
                "Tenant bağlamı bu kapsamda zaten kurulmuş; ikinci kez değiştirilemez.");

        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant kimliği boş olamaz.", nameof(tenantId));

        _backgroundTenantId = tenantId;
    }
}
