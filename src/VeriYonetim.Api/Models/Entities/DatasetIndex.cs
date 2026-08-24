namespace VeriYonetim.Api.Models.Entities;

// Bir veri setinin bir kolonu için kurulmuş arama indeksi.
//
// 24.08 ölçümü (`olcumler/2026-08-24 ifade indeksi (oncesi-sonrasi).md`) 1 milyon
// satırlık bir sette sayısal filtrenin 210 ms'den 0,7 ms'ye indiğini gösterdi — 300 kat.
// Ama aynı ölçüm indeksin bedava olmadığını da gösterdi: dört indeks, içe aktarmayı
// 2,3 kat yavaşlattı. Bu yüzden indeks her kolona kendiliğinden kurulmuyor; kullanıcı
// hangi kolonda arama yaptığını söylüyor.
//
// FİZİKSEL indeks kolon ADI bazında, tablo genelinde kuruludur — kayıt ise veri seti
// bazında. Sebebi şu: satırlar tek bir tabloda durur, dolayısıyla "Satislar setinin
// sehir kolonu" diye ayrı bir indeks kurulamaz. Aynı kolon adını kullanan ikinci bir
// set aynı indeksten faydalanır ve indeks ikinci kez kurulmaz; son kayıt silindiğinde
// fiziksel indeks de düşürülür (referans sayımı, bkz. DatasetIndexService).
public class DatasetIndex
{
    public Guid Id { get; set; }

    public Guid DatasetId { get; set; }
    public Dataset Dataset { get; set; } = null!;

    // Hangi kolon hızlandırıldı.
    public string ColumnName { get; set; } = null!;

    // Kolonun şemadaki tipi. İndekslenen ifade buna göre değişir: metin `lower(...)`,
    // sayı `(...)::numeric`. Kayıtta durmasının sebebi, indeksi düşürürken şemaya
    // yeniden bakmak zorunda kalmamak — kolon o arada silinmiş olabilir.
    public string ColumnType { get; set; } = null!;

    // PostgreSQL'deki fiziksel indeksin adı. Kolon adı + tipten türetilir, çünkü aynı
    // adı taşıyan kolonlar aynı indeksi paylaşır.
    public string IndexName { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
