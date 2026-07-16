namespace VeriYonetim.Api.Models.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string Role { get; set; } = "User";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
}
