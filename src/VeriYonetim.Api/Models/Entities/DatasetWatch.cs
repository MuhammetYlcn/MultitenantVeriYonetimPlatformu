namespace VeriYonetim.Api.Models.Entities;

/// <summary>
/// Kaydedilmiş bir soru ve ona bağlı eşik — sistemin kendiliğinden konuştuğu tek yer.
///
/// Bugüne kadarki her şey ÇEKME üzerine kuruluydu: kullanıcı sorar, sistem cevaplar.
/// İzleyici bunu tersine çevirir; soru bir kez sorulur, sonrasında cevabı sistem takip
/// eder ve eşik geçildiğinde kendisi haber verir.
///
/// FİRMAYA ait, kullanıcıya değil. Sohbetler kişiseldir (bkz. AskConversation) ama bir
/// uyarı iş meselesidir: "stok kritik seviyenin altına düştü" haberini yalnız o soruyu
/// yazan kişinin görmesi, o kişi izinli olduğunda kimsenin haberi olmaması demek olurdu.
/// </summary>
public class DatasetWatch
{
    public Guid Id { get; set; }

    /// İzolasyonun anahtarı. Arka plandaki koşu bağlamını bu alandan kurar
    /// (bkz. ITenantContextSetter) — belge işiyle aynı desen.
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// İzleyiciyi kuran kişi. Sahiplik değil, İZ: uyarıyı firmanın tamamı görür, ama
    /// "bunu kim kurmuş" sorusunun cevabı listede durmalı.
    ///
    /// Nullable ve kullanıcı silinince NULL'a düşüyor — izleyici onunla birlikte GİTMİYOR.
    /// İşten ayrılan birinin kurduğu alarmın sessizce yok olması, tam da bu adımın
    /// kapatmak istediği "haber vermeden susma" hâlidir.
    /// </summary>
    public Guid? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// Listede görünen ad. Sorudan türetilir, kullanıcı değiştirebilir.
    public string Title { get; set; } = null!;

    /// Kullanıcının yazdığı özgün soru. Saklanıyor çünkü plan JSON'u insan için okunur
    /// değil; "bu izleyici neyi ölçüyor" sorusunun cevabı budur.
    public string Question { get; set; } = null!;

    /// <summary>
    /// Sorunun DOĞRULANMIŞ planı — izleyicinin çalışma biçimini belirleyen asıl karar.
    ///
    /// Tekrar koşuda modele SORULMAZ. İlk seferde üretilmiş ve o an çalıştırılarak
    /// doğrulanmış plan burada durur; her koşuda aynı plan çalışır. Üç kazancı var:
    ///   1. Koşu milisaniyelerde biter — 30 saniyelik bir model çağrısı yok.
    ///   2. Aynı soru her seferinde AYNI ŞEYİ ölçer; değer geçmişi ancak böyle anlamlı
    ///      olur. Model her koşuda yeniden yorumlasaydı grafikteki kırılmanın verideki
    ///      değişiklikten mi modelin fikir değiştirmesinden mi geldiği bilinemezdi.
    ///   3. Ollama kuyruğu kullanıcının gerçek sorularıyla yarışmaz.
    /// </summary>
    public string PlanJson { get; set; } = null!;

    // NOT: burada bir `Model` alanı vardı ("planı üreten model, ölçüm sonradan
    // tartışılırsa hangi modelin yorumu olduğu bilinsin"). Kod incelemesinde ölü olduğu
    // görüldü: Create onu hiç yazmıyordu ve AskMessage'da da böyle bir alan yok, yani
    // kolon her satırda boştu. Bir izlenebilirlik güvencesini kodda karşılığı olmadan
    // vaat etmektense alanı kaldırmak doğru — belgede de bu şekilde anlatılıyor.

    /// <summary>
    /// Planın düz Türkçe okunuşu ("şöyle anladım") — kurulduğu andaki hâliyle saklanır.
    ///
    /// Her görüntülemede yeniden üretilebilirdi ama o zaman kırık bir izleyicinin ne
    /// ölçtüğü de görüntülenemezdi; oysa "neden kırıldı" sorusuna bakan kullanıcının tam
    /// olarak buna ihtiyacı var.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// Koşu sıklığı, dakika. Serbest sayı değil (bkz. WatchIntervals): dakikada bir koşan
    /// bir izleyici veritabanını yorar ve hiçbir iş sorusu o çözünürlükte değişmez.
    public int IntervalMinutes { get; set; }

    /// <see cref="WatchConditionKind"/>: mutlak değer mi, önceki koşuya göre değişim mi.
    public string ConditionKind { get; set; } = WatchConditionKind.Value;

    /// <see cref="WatchConditionOps"/>: gt | gte | lt | lte.
    public string ConditionOp { get; set; } = null!;

    /// Eşik. ConditionKind = change ise YÜZDE (20 = %20).
    public decimal Threshold { get; set; }

    /// Kapatılan izleyici koşmaz ama silinmez: tatilde susturmak ile vazgeçmek aynı şey değil.
    public bool IsEnabled { get; set; } = true;

    /// <see cref="WatchStatus"/>. "broken" bu alanın asıl varlık sebebi — bkz. aşağısı.
    public string Status { get; set; } = WatchStatus.Ok;

    /// <summary>
    /// İzleyici neden çalışmıyor. Status = broken iken dolu.
    ///
    /// Kırık izleyici SESSİZ KALMAZ. Kaydedilmiş plan, dayandığı kolon silinince ya da
    /// veri seti adı değişince çalışmaz hâle gelir. O durumda sıfır değer raporlayıp
    /// "eşik aşılmadı" demek, kullanıcıyı çalışmayan bir alarma güvendirmek olurdu —
    /// projenin baştan beri kovaladığı sessiz yanlış cevabın alarm hâli. Bu yüzden
    /// izleyici kırık işaretlenir ve bu da bir bildirim doğurur.
    /// </summary>
    public string? Error { get; set; }

    /// Son ölçülen değer. Kırık izleyicide eski değer kalır (silinmez): "en son ne zaman
    /// ne görmüştük" sorusunun cevabı kırıldıktan sonra da lazım.
    public decimal? LastValue { get; set; }

    /// <summary>
    /// Bir önceki ÖLÇÜLEBİLEN koşunun değeri.
    ///
    /// Değişim yüzdesi koşu sırasında <c>LastValue</c> ile hesaplanır (o an henüz
    /// güncellenmemiştir); bu alan ekranda "önceki değer" olarak gösterilir ve izleyici
    /// düzenlendiğinde durumun yeniden değerlendirilmesinde "önceki" olarak kullanılır.
    /// Eski yorumu ("değişim yüzdesi bununla hesaplanır") yanlıştı.
    ///
    /// Tanımsız ölçüm (boş kümede ortalama) bu ikiliyi KAYDIRMAZ: taban silinirse
    /// sonraki gerçek sıçrama karşılaştırmasız kalır ve sessizce yutulur.
    /// </summary>
    public decimal? PreviousValue { get; set; }

    /// <summary>
    /// Peş peşe kaç koşunun ölçülemediği.
    ///
    /// Tek bir başarısız koşu izleyiciyi kırık saymıyor. Sayıyordu ve bedeli tek bir olay
    /// için üç uyarıydı: eşik aşıldı (uyarı 1) → bir koşuda veritabanı bağlantısı titredi,
    /// kırık (uyarı 2) ve eşik durumu sıfırlandı → bir sonraki koşuda değer hâlâ aynı,
    /// "yeni geçiş" sanıldı (uyarı 3). Veride hiçbir şey değişmeden kullanıcı üç e-posta
    /// alıyordu; günde birkaç kez titreyen bir bağlantıda bu, alarmın kendisini
    /// gürültüye çeviriyordu. Ayrıca o uyarılar okunmamış olarak biriktiği ve bakım işi
    /// okunmamışları hiç silmediği için 500 koşuluk tavan da fiilen deliniyordu.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Şu an eşiğin İÇİNDE mi. Bildirim KENAR TETİKLEMELİ: uyarı yalnız durum
    /// değiştiğinde (aşılmadı → aşıldı) gider.
    ///
    /// Sebebi alarm yorgunluğu: saatlik koşan bir izleyicide eşik bir kez aşıldıktan
    /// sonra her saat aynı uyarıyı göndermek, kullanıcının üçüncü günden sonra bütün
    /// uyarıları görmezden gelmesiyle sonuçlanır — yani alarmın kendisini bozar. Değer
    /// eşiğin dışına çıkıp tekrar içine girerse yeni bir olaydır ve yeniden haber verilir.
    /// </summary>
    public bool IsBreaching { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }

    /// Sıradaki koşunun zamanı. Zamanlayıcı bu alana bakarak süresi gelenleri toplar;
    /// her izleyici için ayrı bir Hangfire kaydı açmak yerine tek bir tarama koşuyor.
    public DateTime NextRunAt { get; set; } = DateTime.UtcNow;

    /// En son ne zaman uyarı doğurdu (kırılma dahil).
    public DateTime? LastTriggeredAt { get; set; }

    public List<DatasetWatchRun> Runs { get; set; } = new();
}

/// <summary>
/// Tek bir koşunun kaydı — hem değer geçmişi grafiğinin kaynağı hem bildirim kutusu.
///
/// Ayrı bir uyarı tablosu açılmadı: bir uyarı zaten bir koşunun sonucudur ve ikisini
/// ayırmak aynı anı iki tabloda tarif etmek olurdu. Bildirilen koşu <see cref="Notified"/>
/// ile işaretlenir; okunmamış rozeti bunların okunmamışlarını sayar.
/// </summary>
public class DatasetWatchRun
{
    public Guid Id { get; set; }

    public Guid WatchId { get; set; }
    public DatasetWatch Watch { get; set; } = null!;

    public DateTime RanAt { get; set; } = DateTime.UtcNow;

    /// Ölçülen değer. Koşu düştüyse null — ve bu null SIFIR DEĞİLDİR: grafikte boşluk
    /// olarak görünür, çünkü "ölçemedik" ile "sıfır ölçtük" aynı şey değil.
    public decimal? Value { get; set; }

    /// Bu koşuda eşik aşıldı mı.
    public bool Breached { get; set; }

    /// Koşu düştüyse sebebi. Dolu olması izleyicinin kırık olduğu anlamına gelir.
    public string? Error { get; set; }

    /// Bu koşu kullanıcıya bildirildi mi (kenar tetikleme sonucu).
    public bool Notified { get; set; }

    /// Bildirim görüldü mü. Firma geneli: uyarı firmaya ait olduğundan kim gördüyse
    /// görülmüş sayılır, herkes ayrı ayrı kapatmak zorunda kalmaz.
    public DateTime? ReadAt { get; set; }
}

public static class WatchConditionKind
{
    /// Ölçülen değerin kendisi eşikle karşılaştırılır.
    public const string Value = "value";

    /// Önceki koşuya göre YÜZDE değişim eşikle karşılaştırılır. İlk koşuda karşılaştırma
    /// yapılmaz (önceki değer yok) — uyarı doğurmaz, yalnız taban değer kaydedilir.
    public const string Change = "change";

    public static bool IsValid(string? kind) => kind is Value or Change;
}

public static class WatchConditionOps
{
    public static readonly IReadOnlyList<string> All = new[] { "gt", "gte", "lt", "lte" };

    public static bool IsValid(string? op) => op is not null && All.Contains(op);

    public static bool Matches(string op, decimal left, decimal right) => op switch
    {
        "gt" => left > right,
        "gte" => left >= right,
        "lt" => left < right,
        "lte" => left <= right,
        _ => false
    };
}

public static class WatchStatus
{
    /// Çalışıyor, eşik aşılmadı.
    public const string Ok = "ok";

    /// Çalışıyor, eşik aşıldı.
    public const string Breaching = "breaching";

    /// ÇALIŞMIYOR. Sıfır raporlamak yerine bu duruma düşülür (bkz. DatasetWatch.Error).
    public const string Broken = "broken";
}

public static class WatchIntervals
{
    /// İzin verilen koşu sıklıkları: 15 dk, saatlik, 6 saatlik, günlük.
    ///
    /// Serbest sayı yerine sabit liste: alt sınır olmasa kullanıcı dakikada bir koşan bir
    /// izleyici kurabilirdi. Bunun ölçtüğü hiçbir iş sorusu o çözünürlükte değişmez, ama
    /// veritabanına düşen yük gerçektir.
    public static readonly IReadOnlyList<int> All = new[] { 15, 60, 360, 1440 };

    public static bool IsValid(int minutes) => All.Contains(minutes);
}
