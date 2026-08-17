namespace VeriYonetim.Api.Models.Entities;

/// <summary>
/// Bir belge okuma işinin kaydı — asenkron belge işlemenin merkezi.
///
/// Neden Hangfire'ın kendi tablosu yetmiyor: Hangfire bir işin çalışıp çalışmadığını
/// bilir, ama iki şeyi bilmez. Birincisi SONUÇ — belgeden çıkan tabloyu onay ekranına
/// vermek gerekir. İkincisi ve önemlisi YETKİ: Hangfire tabloları tenant kavramı
/// tanımaz, oysa bir işin durumunu yalnız onu başlatan firma görebilmelidir. Bu yüzden
/// iş kaydı kendi tablomuzda duruyor ve global query filter'a giriyor.
/// </summary>
public class DocumentJob
{
    public Guid Id { get; set; }

    /// İzolasyonun anahtarı. Arka plan işi bağlamını bu alandan kurar (bkz. ITenantContextSetter).
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// İşi başlatan kullanıcı — bildirim ona gider, listede kendi işlerini görür.
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <see cref="DocumentJobKind"/>: şemalı çıkarım mı, şemasız keşif mi.
    public string Kind { get; set; } = null!;

    /// Hedef veri seti — yalnız çıkarım geçişinde doludur. Keşifte hedef henüz bilinmez.
    public Guid? DatasetId { get; set; }
    public Dataset? Dataset { get; set; }

    /// <see cref="DocumentJobStatus"/>.
    public string Status { get; set; } = DocumentJobStatus.Queued;

    /// Sonucun kendisi (DocumentExtractionResponse ya da DocumentDiscoveryResponse), JSON.
    ///
    /// Neden tek bir JSON kolonu: iki geçiş iki farklı sonuç şekli üretiyor ve bu şekil
    /// zaten istemciye olduğu gibi gidiyor. Kolonlara açmak, aynı DTO'yu ikinci kez —
    /// bu sefer tabloda — tarif etmek olurdu.
    public string? ResultJson { get; set; }

    /// Kullanıcıya gösterilecek hata mesajı. Yığın izi TAŞINMAZ: mesaj arayüzde görünüyor.
    public string? Error { get; set; }

    /// <summary>
    /// Belgenin GÖSTERİM için saklanan hâli — küçültülmüş, orijinal değil.
    ///
    /// Neden sunucuda duruyor: asenkron akışın bütün amacı kullanıcının işi başlatıp
    /// ekrandan çıkabilmesi. Geri döndüğünde onay ekranı belgeyi hücrelerin yanında
    /// göstermek zorunda, ama istemcinin elindeki dosya çoktan gitmiştir.
    ///
    /// Neden veritabanında: görüntü kalıcı bir varlık değil, işin ömrüyle sınırlı bir ARA
    /// ÜRÜN — onaydan sonra silinir. Kısa ömürlü olduğu için dosya sisteminin sunacağı
    /// kazanç doğmuyor, buna karşılık iki ayrı depoyu tutarlı tutma yükü (kayıt silindi
    /// dosya kaldı, dosya silindi kayıt kaldı) tamamen kalkıyor: kayıt gidince görüntü de
    /// aynı işlemde gidiyor. Erişim de query filter'dan geçtiği için başka firmanın
    /// belgesini isteme denemesi sorguda ölüyor, elle yol denetimi gerekmiyor.
    /// </summary>
    public byte[]? Image { get; set; }
    public string? ImageContentType { get; set; }
    public string? FileName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Bu işten çıkan tablonun veri setine YAZILDIĞI an.
    ///
    /// Neden ayrı bir alan gerekti: iş listesi kalıcı olduğu için kullanıcı onayladığı bir
    /// belgeyi tekrar açabiliyor ve ekranda tablo yine duruyor. İkinci kez kaydederse aynı
    /// satırlar sete İKİNCİ KEZ eklenir — sessiz mükerrer veri. Görüntünün silinmiş olması
    /// bunu anlamak için yeterli değildi: süresi dolan görüntüler de siliniyor, yani o
    /// dolaylı işaret "onaylandı" ile "eskidi" durumlarını ayırt edemiyor.
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }
}

public static class DocumentJobKind
{
    /// Hedef şema biliniyor: model o setin kolonlarını arar.
    public const string Extract = "extract";

    /// Hedef şema yok: model kolonları kendisi çıkarır, sonra var olan setlerle eşleştirilir.
    public const string Discover = "discover";
}

public static class DocumentJobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// Bitmiş iş: artık durumu değişmez, sonucu (ya da hatası) kesindir.
    public static bool IsFinal(string status) => status is Succeeded or Failed;
}
