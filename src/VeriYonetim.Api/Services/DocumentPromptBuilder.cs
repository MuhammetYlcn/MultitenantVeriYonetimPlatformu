using System.Text;

namespace VeriYonetim.Api.Services;

// Görsel modele gönderilen istemi kurar.
//
// Tek cümlelik gerekçe: SERBEST ÇIKARIM KULLANILAMAZ. Ölçümde modele yalnız "bilgileri
// çıkar" dendiğinde alan adlarını uydurdu (`firmasi_adi`, `kdv_tutarı`), ara toplamı
// `toplam_miktar` diye en üste koydu, alıcıyı `satıcı_adresi` sandı. Her belgede değişen
// ad bir veri setine yazılamaz. Hedef şema verildiğinde adlara birebir uyuyor ve alan
// doğruluğu 9/9 çıkıyor.
//
// Bu, doğal dilde sorgu adımındaki kararın aynısı (bkz. QueryPromptBuilder): model serbest
// metin değil, VERİLEN YAPIYI doldurur.
public static class DocumentPromptBuilder
{
    public static string Build(IReadOnlyList<ColumnSchema> schema)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Bu bir Türkçe ticari belge görüntüsü. İçindeki bilgileri JSON olarak çıkar.");
        sb.AppendLine();

        sb.AppendLine("Alanlar (tam bu adlarla, başka ad uydurma):");
        foreach (var column in schema)
            sb.AppendLine($"- {column.Name} ({TypeLabel(column.Type)})");
        sb.AppendLine();

        sb.AppendLine("Kurallar:");
        sb.AppendLine("- Alan adlarını AYNEN yukarıdaki gibi yaz.");
        sb.AppendLine("- Belgede bulamadığın alanı null yaz, tahmin etme.");

        // Kalem/belge ayrımını modele bırakıyoruz çünkü hedef setin hangi kolonunun belge
        // düzeyi hangisinin satır düzeyi olduğu şemadan anlaşılmıyor. Ayrıştırıcı iki yapıyı
        // da kabul ediyor ve ToParsedTable belge alanlarını her kalem satırına tekrarlıyor,
        // yani yanlış yere koyması veri kaybına yol açmıyor.
        sb.AppendLine("- Belgede kalem/ürün tablosu varsa her satırı \"kalemler\" dizisine yaz.");
        sb.AppendLine("  Tabloya ait olmayan, belgenin tamamı için geçerli bilgileri \"alanlar\"a yaz.");
        sb.AppendLine("- Belgede kalem tablosu yoksa \"kalemler\": [] yaz. Kalem UYDURMA.");
        sb.AppendLine("- Tablodaki hiçbir satırı atlama, satırları birleştirme, sıra değiştirme.");

        // Ölçümde görülen davranış: tabloda başlık satırı yoksa model İLK VERİ SATIRINI
        // kolon adı sanıp harcıyor (24 belgenin 11'inde bir satır eksik). İstem ayarı bunu
        // kapatamadı (v1 13/24, v2 5/24, v3 13/24); ölçümde çözen şey girdiyi değiştirmek
        // oldu (sentetik başlık şeridi, %51 → %94). Yine de bu tek maddelik uyarı v3'ün
        // hedefiydi ve zarar vermedi, o yüzden duruyor.
        sb.AppendLine("- Tabloda başlık satırı olmayabilir. Böyle bir tabloda EN ÜSTTEKİ satır da");
        sb.AppendLine("  bir veri satırıdır; onu kolon adı sanıp atlama.");

        sb.AppendLine("- Sayıları ondalık NOKTA ile yaz: 1.500,75 -> 1500.75");
        sb.AppendLine("- Tarihleri YYYY-AA-GG biçiminde yaz.");
        sb.AppendLine("- Yalnız JSON döndür; açıklama, yorum veya kod bloğu işareti yazma.");
        sb.AppendLine();

        sb.Append("Biçim: {\"alanlar\": {...}, \"kalemler\": [{...}]}");

        return sb.ToString();
    }

    /// KEŞİF geçişinin istemi: hedef şema YOK, adları model seçer.
    ///
    /// Yukarıdaki gerekçenin tersi gibi duruyor ama değil. Şemalı istem, belgenin hangi
    /// sete yazılacağı BİLİNDİĞİNDE kullanılır. İlk kez görülen bir belgede böyle bir set
    /// yoktur; birini seçmek için önce belgede ne yazdığını görmek gerekir. Yani buradaki
    /// çıktı veri değil TASLAKTIR: kolon adları SchemaMatcher'a girer, oradan çıkan öneri
    /// kullanıcıya sunulur, kaydetme kararı onay ekranında verilir.
    ///
    /// Ölçümde serbest çıkarımın sorunu adların HER BELGEDE DEĞİŞMESİYDİ (`firmasi_adi`,
    /// `kdv_tutarı`). Burada bu bir kusur değil beklenen durum; adları birebir kullanmak
    /// yerine eşleştirdiğimiz için değişkenliğe dayanıklıyız. Yine de aşağıdaki adlandırma
    /// kuralları değişkenliği azaltıyor: eşleşme oranı doğrudan buna bağlı.
    public static string BuildDiscovery()
    {
        var sb = new StringBuilder();

        sb.AppendLine("Bu bir Türkçe ticari belge görüntüsü. İçindeki bilgileri JSON olarak çıkar.");
        sb.AppendLine("Belgede hangi alanlar varsa onları çıkar; hazır bir alan listesi verilmedi.");
        sb.AppendLine();

        sb.AppendLine("Alan adlandırma kuralları:");
        sb.AppendLine("- Alan adları Türkçe, küçük harf ve alt çizgili olsun: fatura_no, urun_adi.");
        sb.AppendLine("- Adı belgedeki etiketten al; etiket yoksa içeriği anlatan kısa bir ad ver.");
        sb.AppendLine("- Ada birim veya para birimi yazma: \"tutar_tl\" değil \"tutar\".");
        sb.AppendLine("- Aynı adı iki kez kullanma.");
        sb.AppendLine();

        sb.AppendLine("Kurallar:");
        sb.AppendLine("- Belgenin türünü \"belge_turu\" alanına tek kelimeyle yaz: fatura, fis, makbuz, irsaliye…");
        sb.AppendLine("- Belgede olmayan bir alanı UYDURMA; emin olmadığın değeri null yaz.");
        sb.AppendLine("- Belgede kalem/ürün tablosu varsa her satırı \"kalemler\" dizisine yaz.");
        sb.AppendLine("  Tabloya ait olmayan, belgenin tamamı için geçerli bilgileri \"alanlar\"a yaz.");
        sb.AppendLine("- Belgede kalem tablosu yoksa \"kalemler\": [] yaz. Kalem UYDURMA.");
        sb.AppendLine("- Tablodaki hiçbir satırı atlama, satırları birleştirme, sıra değiştirme.");

        // Şemalı istemdeki maddenin aynısı. Keşif geçişinde daha da önemli: burada sentetik
        // başlık şeridi eklenemiyor (şerit hedef şemadan üretiliyor, keşifte şema yok), yani
        // ilk satır kaybına karşı elimizde yalnız bu uyarı ve onay ekranı var.
        sb.AppendLine("- Tabloda başlık satırı olmayabilir. Böyle bir tabloda EN ÜSTTEKİ satır da");
        sb.AppendLine("  bir veri satırıdır; onu kolon adı sanıp atlama.");

        sb.AppendLine("- Sayıları ondalık NOKTA ile yaz: 1.500,75 -> 1500.75");
        sb.AppendLine("- Tarihleri YYYY-AA-GG biçiminde yaz.");
        sb.AppendLine("- Yalnız JSON döndür; açıklama, yorum veya kod bloğu işareti yazma.");
        sb.AppendLine();

        sb.Append("Biçim: {\"belge_turu\": \"...\", \"alanlar\": {...}, \"kalemler\": [{...}]}");

        return sb.ToString();
    }

    private static string TypeLabel(string type) => type switch
    {
        "number" => "sayı",
        "date" => "tarih",
        _ => "metin"
    };
}
