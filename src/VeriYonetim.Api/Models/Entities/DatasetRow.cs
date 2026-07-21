namespace VeriYonetim.Api.Models.Entities;

// Bir veri setinin tek bir satırı. Değerler sabit kolonlara değil, tek bir jsonb
// alanına ({"ad": "Ali", "yas": 30}) yazılır — böylece her tenant'ın farklı kolonları
// aynı fiziksel tabloda, DDL üretmeden tutulur. Dataset'in çocuğu: kendi TenantId'si
// yok, izolasyon Dataset üzerinden (navigation) sağlanır (DatasetColumn deseni).
public class DatasetRow
{
    public Guid Id { get; set; }

    // Kolon adı → değer. number → decimal, date → DateTime, text → string, boş → null.
    // DbContext'te jsonb kolona map edilir (ValueConverter ile JSON string'e çevrilerek).
    public Dictionary<string, object?> Data { get; set; } = new();

    public Guid DatasetId { get; set; }
    public Dataset Dataset { get; set; } = null!;
}
