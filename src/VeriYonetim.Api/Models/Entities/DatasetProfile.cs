namespace VeriYonetim.Api.Models.Entities;

// Bir veri setinin kolon profillerinin ÖNBELLEĞİ: her kolonun benzersiz olup olmadığı
// ve taşıdığı değerler (bkz. RelationDetector).
//
// Neden var? İlişki algılaması, yeni içeri alınan seti firmanın diğer setleriyle
// karşılaştırır ve bunun için her setin her kolonunu ayrı bir GROUP BY ile profiller.
// 21.08 ölçümü bu profillemenin içe aktarmaya SABİT ~5 saniye eklediğini gösterdi:
// 1.000 satırlık bir dosya yükleyen kullanıcı 5,5 saniyenin 5,4'ünü, hiç değişmemiş
// komşu setlerin baştan profillenmesini bekleyerek geçiriyordu.
//
// Komşu setin verisi değişmediyse profili de değişmez. Bu tablo o profili saklar;
// içe aktarma yalnız GERÇEKTEN değişmiş setleri yeniden profiller.
//
// Önbellek bir KOLAYLIKTIR: kaydı olmayan ya da okunamayan set yeniden profillenir,
// sonuç aynıdır — yalnız daha yavaş.
public class DatasetProfile
{
    // Set başına tek satır: birincil anahtar aynı zamanda yabancı anahtar.
    public Guid DatasetId { get; set; }
    public Dataset Dataset { get; set; } = null!;

    // Profil çıkarıldığı andaki setin damgası (UpdatedAt ?? CreatedAt). Setin bugünkü
    // damgası bundan farklıysa önbellek bayattır ve kullanılmaz.
    //
    // Neden ayrı bir "veri sürümü" alanı değil de bu damga: satır yazan her uç zaten
    // UpdatedAt'i güncelliyor. Yeni bir yazma yolu eklendiğinde damgayı güncellemeyi
    // unutmak, bayat profilin kullanılması demek olurdu — yani sessizce yanlış ilişki.
    // Ada değiştirmek de damgayı ilerletir; bedeli bir kereliğine boşuna profilleme,
    // yani hata güvenli yönde.
    public DateTime Stamp { get; set; }

    // Profillerin JSON'u (jsonb). Şeması RelationDetector'ın içindedir; başka kimse
    // okumaz, bu yüzden ayrı kolonlara açılmadı.
    public string Json { get; set; } = null!;

    public DateTime ComputedAt { get; set; }
}
