namespace VeriYonetim.Api.Services;

/// <summary>
/// İki ayrı kimlik dünyası vardır ve birbirinin uçlarına giremezler:
///
///   • Tenant kullanıcısı  → token'da tenant_id claim'i taşır  → /api/datasets, /api/users
///   • Platform yöneticisi → token'da platform_admin claim'i    → /api/platform/*
///
/// Bu ayrım rol ile DEĞİL politika ile kurulur. Sebebi: düz [Authorize] "geçerli
/// imzalı herhangi bir token" demektir; platform token'ı da geçerli imzalıdır.
/// Politika, claim'in varlığını şart koşarak yanlış dünyanın token'ını 403 ile
/// eler — tenant uçlarında "tenant_id yok" durumu sessizce boş sonuca düşmez.
/// </summary>
public static class AuthPolicies
{
    public const string TenantUser = "TenantUser";
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>Platform token'ını işaretleyen claim (değeri "true").</summary>
    public const string PlatformAdminClaim = "platform_admin";

    /// <summary>Tenant token'ındaki firma kimliği claim'i.</summary>
    public const string TenantIdClaim = "tenant_id";
}
