namespace VeriYonetim.Api.Models.Entities;

// Bir veri setinin kolon tanımı (algılanan şema). Dataset'in çocuğu — kendi TenantId'si
// yok; izolasyon Dataset üzerinden (navigation) sağlanır.
public class DatasetColumn
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;    // kolon adı, örn. "yas"
    public string Type { get; set; } = null!;    // "text" | "number" | "date"
    public int Ordinal { get; set; }             // kolon sırası (0,1,2…)

    public Guid DatasetId { get; set; }
    public Dataset Dataset { get; set; } = null!;
}
