namespace VeriYonetim.Api.Models.Entities;

/// <summary>
/// Platform yöneticisinin tenant'lar üzerinde yaptığı işlemlerin izi
/// (kim, ne zaman, hangi firmaya, ne yaptı).
///
/// Bilinçli tasarım: TargetTenantId düz bir Guid — Tenant'a foreign key YOK.
/// Denetim kaydı, hedef firma silinse bile ayakta kalmalı; FK olsaydı cascade
/// ile silinir ya da silmeyi bloklardı. Aynı sebeple firma adı ve yönetici
/// e-postası da kaydın içine kopyalanır (denormalize) — sonradan değişse/silinse
/// bile kaydın o anki gerçeği korunur.
/// </summary>
public class PlatformAuditLog
{
    public Guid Id { get; set; }

    public Guid PlatformAdminId { get; set; }
    public string PlatformAdminEmail { get; set; } = null!;

    /// <summary>"TenantSuspended" | "TenantActivated" | "PlatformLogin"</summary>
    public string Action { get; set; } = null!;

    public Guid? TargetTenantId { get; set; }
    public string? TargetTenantName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
