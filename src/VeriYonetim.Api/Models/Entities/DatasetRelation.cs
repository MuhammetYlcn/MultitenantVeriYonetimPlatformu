namespace VeriYonetim.Api.Models.Entities;

// İki veri seti arasındaki bağ: "Satışlar.musteri_no ↔ Müşteriler.no".
//
// Neden ayrı bir varlık? Sistem bu bağı kendiliğinden BİLEMEZ. Her veri seti tek başına
// yüklenir; hangi kolonun hangi sete işaret ettiği ancak kullanıcının söylemesiyle
// bilinir. JOIN'in eksik parçası bir sorgu yeteneği değil, işte bu bilgiydi.
//
// Yön taşımaz: ilişki simetriktir, sorgu hangi setten başlarsa diğerine bağlanır.
public class DatasetRelation
{
    public Guid Id { get; set; }

    public Guid FromDatasetId { get; set; }
    public Dataset FromDataset { get; set; } = null!;
    public string FromColumn { get; set; } = null!;

    public Guid ToDatasetId { get; set; }
    public Dataset ToDataset { get; set; } = null!;
    public string ToColumn { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
