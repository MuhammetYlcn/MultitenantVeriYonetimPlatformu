namespace VeriYonetim.Api.Models.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public string SchemaName { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Firma askıya alındığında false olur: kullanıcıları artık giriş yapamaz ve
    // oturum yenileyemez. Veri SİLİNMEZ — abonelik bitişi/sözleşme askısı geri
    // alınabilir bir durumdur. Yalnızca platform yöneticisi değiştirebilir.
    public bool IsActive { get; set; } = true;
    public DateTime? SuspendedAt { get; set; }

    public List<User> Users { get; set; } = new();
}
