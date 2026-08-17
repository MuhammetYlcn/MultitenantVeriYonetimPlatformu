using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Hubs;

/// <summary>
/// İş durumu bildirimlerinin canlı kanalı.
///
/// Neden yoklama (polling) değil: belge okuma 30-150 saniye sürüyor ve süre baştan
/// bilinmiyor. Yoklamada istemci ya sık sorar (boşa giden istek yığını) ya seyrek sorar
/// (kullanıcı bitmiş işi geç görür). Açık bir kanalda sunucu hazır olduğu anda söyler.
///
/// Hub'ın kendisi bilinçli olarak İŞLEVSİZ: istemci buraya bir şey ÇAĞIRMAZ, yalnız
/// dinler. Bütün yazma işleri yetkilendirilmiş REST uçlarından geçer — hub üzerinden iş
/// başlatılabilseydi, yetki denetimini ikinci bir yerde daha doğrulamak gerekirdi.
/// </summary>
[Authorize(Policy = AuthPolicies.TenantUser)]
public class JobsHub : Hub
{
    /// Bir kullanıcının kendi işleri için grup adı.
    public static string UserGroup(Guid userId) => $"user:{userId}";

    /// Firma geneli yayın için grup adı. Bu adımda kullanılmıyor; izleyiciler adımında
    /// eşik bildirimi firmanın tamamına gidecek ve aynı hub'ı kullanacak.
    public static string TenantGroup(Guid tenantId) => $"tenant:{tenantId}";

    public override async Task OnConnectedAsync()
    {
        // Kimlik token'dan geliyor; istemcinin gönderdiği hiçbir değere bakılmıyor.
        // Grup adını istemci seçebilseydi başkasının bildirimlerine abone olabilirdi.
        if (Guid.TryParse(Context.User?.FindFirstValue("sub"), out var userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));

        if (Guid.TryParse(Context.User?.FindFirstValue(AuthPolicies.TenantIdClaim), out var tenantId))
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId));

        await base.OnConnectedAsync();
    }

    // Gruptan çıkarma elle yapılmıyor: SignalR bağlantı kapanınca üyelikleri kendisi
    // düşürür (grup üyeliği bağlantıya bağlıdır, kullanıcıya değil).
}
