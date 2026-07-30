using Microsoft.AspNetCore.Mvc;

namespace VeriYonetim.Api.Services;

/// <summary>
/// Platform token'ından yöneticinin kimliğini okur. TenantContext'in platform
/// karşılığı, ama tek bir claim okumaktan ibaret olduğu için servis değil uzantı
/// metodu: istekten gelen değer ASLA gövdeden/query'den alınmaz, hep token'dan.
/// </summary>
public static class PlatformIdentityExtensions
{
    public static Guid? PlatformAdminId(this ControllerBase controller)
    {
        var value = controller.User.FindFirst("sub")?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public static string? PlatformAdminEmail(this ControllerBase controller) =>
        controller.User.FindFirst("email")?.Value;
}
