namespace VeriYonetim.Api.Models.Entities;

// Bir veri setinin "başlık kartı" (katalog kaydı) — verinin kendisini değil,
// metadata'sını tutar. Satırlar (JSONB) ve kolon tanımları sonraki günlerde
// (DatasetRow / DatasetColumn) buna bağlanacak.
public class Dataset
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;        // zorunlu — örn. "2026 Satışları"
    public string? Description { get; set; }          // opsiyonel (nullable)
    public int RowCount { get; set; }                 // içindeki satır sayısı (import'ta dolacak)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }          // son güncelleme (nullable)

    // İzolasyonun anahtarı — global query filter bu alan üzerinden çalışır.
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
}
