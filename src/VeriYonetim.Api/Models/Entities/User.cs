namespace VeriYonetim.Api.Models.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    // 3'lü rol: Viewer (okur) ⊂ Editor (+ veri yazar) ⊂ Admin (+ kullanıcı yönetimi).
    // Varsayılan en düşük yetki: Viewer. Kaydolan ilk kullanıcı AuthService'te Admin yapılır.
    public string Role { get; set; } = "Viewer";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
}
