using System.Security.Claims;

namespace VeriYonetim.Api.Services;

public interface ITenantContext
{
    Guid? TenantId { get; }
}

public class TenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? TenantId
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?
                .User.FindFirstValue("tenant_id");

            return Guid.TryParse(value, out var tenantId) ? tenantId : null;
        }
    }
}
