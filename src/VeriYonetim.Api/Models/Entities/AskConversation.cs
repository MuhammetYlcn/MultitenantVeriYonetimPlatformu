namespace VeriYonetim.Api.Models.Entities;

// Bir kullanıcının soru-cevap oturumu.
//
// Neden saklanıyor? Sorgulama tek seferlik bir iş değil: kullanıcı bir soruyu birkaç gün
// sonra tekrar görmek, aldığı sayıyı doğrulamak ya da nereye baktığını hatırlamak ister.
// Tarayıcı belleğinde tutmak bunu tek cihaza hapsederdi.
//
// KULLANICIYA ait, firmaya değil: sohbetler kişiseldir, aynı firmadaki başka bir kullanıcı
// göremez. İzolasyon User üzerinden (DatasetColumn/DatasetRow ile aynı desen).
public class AskConversation
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    // Listede görünen ad — ilk sorudan türetilir.
    public string Title { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Son mesajın zamanı; liste buna göre sıralanır.
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AskMessage> Messages { get; set; } = new();
}

// Sohbetteki tek bir tur: soru + o soruya verilen tam yanıt.
//
// Yanıt JSON olarak saklanıyor çünkü şekli soruya göre değişiyor (tek değer, tablo,
// grafik, karşılaştırma). Alanlara açmak her yeni sunum türünde migration gerektirirdi;
// üstelik geçmiş kayıtlar ZATEN verildiği hâliyle gösterilmeli, yeniden hesaplanarak değil.
public class AskMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }
    public AskConversation Conversation { get; set; } = null!;

    public string Question { get; set; } = null!;

    // AskResponse'un serileştirilmiş hâli.
    public string ResponseJson { get; set; } = null!;

    /// <summary>
    /// Bu yanıtı üreten sorgu planı (modelin ham çıktısı).
    ///
    /// Neden saklanıyor: kullanıcı bir cevabı görüp "bunu izle" diyebiliyor ve izleyici
    /// tekrar koşarken modele SORMUYOR, kaydedilmiş planı çalıştırıyor. Plan burada
    /// durmasaydı istemcinin onu geri göndermesi gerekirdi — yani izlenen sorgu, ekranda
    /// cevabı gösterilenle aynı olduğunu ispat edemezdi.
    ///
    /// Eski kayıtlarda null: bu alandan önce üretilmiş yanıtlar izlenemez, izleyici kurma
    /// ucu bunu açık bir hatayla söyler.
    /// </summary>
    public string? PlanJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
