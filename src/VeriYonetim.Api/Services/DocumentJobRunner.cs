using System.Globalization;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

public interface IDocumentJobRunner
{
    /// <summary>
    /// Kuyruktaki bir belge işini çalıştırır. Hangfire bu metodu çağırır.
    /// </summary>
    /// Yeniden deneme KAPALI ve bu bilinçli bir karar. Bir belge okuma denemesi ekran
    /// kartını 30-150 saniye meşgul ediyor; başarısız bir işi arka planda sessizce on kez
    /// daha denemek, kullanıcının haberi olmadan yarım saatlik GPU zamanı harcamak
    /// demektir. Üstelik en sık hata (model servisi kapalı, belge okunamadı) tekrarla
    /// düzelmez. Yeniden deneme kullanıcının kararı: ekranda hata mesajını görür ve
    /// isterse yeniden yükler.
    ///
    /// KUYRUK AYRIMI. Bu iş ayrı bir kuyrukta ("documents") çalışıyor; öncesinde
    /// `Queues` ayarında adı geçiyordu ama HİÇBİR ŞEY o kuyruğa atanmıyordu — yani belge
    /// işleri de, bakım ve izleyici taraması da tek bir "default" kuyruğunda sıraya
    /// giriyordu. Tek işçiyle bunun bedeli ölçülebilir bir güvence ihlaliydi: kullanıcı
    /// 20 fatura yüklediğinde işçi ~40 dakika boyunca belgelerle meşgul oluyor, bu sürede
    /// biriken izleyici taramaları hiç koşmuyor ve "bir izleyici en fazla beş dakika geç
    /// çalışır" sözü tutmuyordu. Kritik stok uyarısı yarım saatten fazla gecikebiliyordu.
    [Queue(DocumentQueue)]
    [AutomaticRetry(Attempts = 0)]
    Task RunAsync(Guid jobId);

    /// Belge işlerinin kuyruğu. Hangfire kuyruk adlarında yalnız küçük harf ve alt
    /// çizgi kabul ediyor.
    public const string DocumentQueue = "documents";
}

/// <summary>
/// Belge işinin arka plandaki yürütücüsü — bu adımın kalbi.
///
/// İstek yolundan tek ama kritik farkı var: HTTP isteği YOK. Bu yüzden firma kimliği
/// token'dan okunamaz ve <see cref="ITenantContext"/> boş döner. Bağlam elle kurulmazsa
/// bütün global query filter'lar "TenantId == null" hâline düşer ve iş hiçbir veri
/// bulamaz. Aşağıdaki sıra bu yüzden gevşetilemez.
/// </summary>
public class DocumentJobRunner : IDocumentJobRunner
{
    /// <summary>
    /// Keşifte karşılaştırılacak en fazla set sayısı.
    ///
    /// 25'ten 200'e çıkarıldı. Eski sınır sessiz bir yanlış cevap üretiyordu: 40 veri seti
    /// olan bir firmada aylardır dokunulmamış "Tedarikçi Faturaları" seti son 25'in
    /// dışında kalıyor, ekran "uyan veri seti bulunamadı" deyip YENİ SET öneriyordu.
    /// Kullanıcı öneriye güvenip yeni set açınca aynı veri iki sete bölünüyordu — ki bu,
    /// SchemaMatcher'ın önlemek için yazıldığı durumun ta kendisi. Sıralama (en son
    /// dokunulan önce) korunuyor, ama sınır artık gerçek bir firmanın ulaşamayacağı bir
    /// yerde: eşleme bellek içi bir ad karşılaştırması, 200 set için de milisaniyeler.
    /// </summary>
    private const int MaxCandidateDatasets = 200;

    private static readonly CultureInfo TurkishCulture = new("tr-TR");

    private readonly AppDbContext _db;
    private readonly ITenantContextSetter _tenantSetter;
    private readonly IDocumentVisionService _vision;
    private readonly IDatasetImportService _importService;
    private readonly IJobNotifier _notifier;
    private readonly ILogger<DocumentJobRunner> _logger;

    public DocumentJobRunner(AppDbContext db, ITenantContextSetter tenantSetter,
        IDocumentVisionService vision, IDatasetImportService importService,
        IJobNotifier notifier, ILogger<DocumentJobRunner> logger)
    {
        _db = db;
        _tenantSetter = tenantSetter;
        _vision = vision;
        _importService = importService;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task RunAsync(Guid jobId)
    {
        // ADIM 1 — iş kaydını FİLTRESİZ oku.
        //
        // Yumurta-tavuk: kaydı bulmak için firma kimliği gerekiyor, firma kimliği ise o
        // kaydın içinde. Zinciri kıracak tek bir yer olmalı ve burası. Filtresiz okumanın
        // güvenli olmasının sebebi, aranan değerin tahmin edilemez bir kimlik (Guid)
        // olması ve buraya kullanıcıdan değil KUYRUKTAN gelmesi — iş kimliğini bu sisteme
        // koyan yine bu sistemdir.
        var job = await _db.DocumentJobs
            .IgnoreQueryFilters()
            .Include(j => j.Tenant)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job is null)
        {
            // Kullanıcı beklerken seti ya da hesabı silinmiş olabilir; iş de onunla gitmiştir.
            _logger.LogWarning("Belge işi bulunamadı, atlanıyor: {JobId}", jobId);
            return;
        }

        // Askıya alınmış firmanın işi ÇALIŞMAZ.
        //
        // WatchScheduler bu denetimi yapıyordu, burası yapmıyordu — aynı sistemde iki
        // farklı kural. Askıdan hemen önce kuyruğa girmiş bir belge askıdan sonra da
        // çalışıp sonucunu yazıyordu: hesabı kapatılmış bir firma için model çalıştırmak
        // ve o firmanın setine veri hazırlamak, askının anlamıyla çelişiyor.
        //
        // İş SİLİNMİYOR, başarısız işaretleniyor: askı geri alınabilir bir durum ve
        // kullanıcı geri döndüğünde ne olduğunu görebilmeli.
        if (!job.Tenant.IsActive)
        {
            _logger.LogInformation(
                "Belge işi {JobId} askıya alınmış firmaya ait, çalıştırılmadı.", jobId);

            job.Status = DocumentJobStatus.Failed;
            job.Error = "Firma askıya alınmış; belge işlenmedi.";
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return;
        }

        // ADIM 2 — bağlamı kur. Bundan SONRAKİ her sorgu bu firmayla sınırlı.
        _tenantSetter.SetForBackgroundWork(job.TenantId);

        // Aynı iş iki kez kuyruğa girmişse (elle tetikleme, yeniden başlatma) ikincisi
        // çalışmasın: bitmiş bir işin sonucunu ikinci bir model çağrısıyla ezmek, kullanıcının
        // onay ekranında baktığı tabloyu altından çekerdi.
        if (job.Status != DocumentJobStatus.Queued)
        {
            _logger.LogInformation("Belge işi {JobId} zaten {Status} durumunda, atlanıyor.",
                jobId, job.Status);
            return;
        }

        // GEÇİŞ KOŞULLU VE ATOMİK. Yukarıdaki denetim oku-sonra-yaz biçimindeydi: aynı iş
        // iki işçiye birden düşerse ikisi de `Queued` okuyup geçebilir, ikisi de modeli
        // çağırır ve ikisi de ResultJson yazar — son yazan kazanır, yani kullanıcı onay
        // ekranında A tablosuna bakarken tablo B'ye dönüşür. Bugün işçi sayısı bir olduğu
        // için tetiklenmiyor, ama ayar (`Hangfire:WorkerCount`) ya da ikinci bir API
        // örneği bunu açardı; "ikinci çalıştırma atlanır" güvencesi o zaman sessizce
        // bozulurdu. Koşullu UPDATE etkilenen satır sayısını döndürüyor: 0 ise başkası
        // aldı demektir.
        var started = DateTime.UtcNow;

        var claimed = await _db.DocumentJobs
            .IgnoreQueryFilters()
            .Where(j => j.Id == jobId && j.Status == DocumentJobStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, DocumentJobStatus.Running)
                .SetProperty(j => j.StartedAt, started));

        if (claimed == 0)
        {
            _logger.LogInformation("Belge işi {JobId} başka bir işçi tarafından alınmış, " +
                                   "atlanıyor.", jobId);
            return;
        }

        // Bellekteki varlık da güncelleniyor: ExecuteUpdate değişiklik takibini bilmiyor.
        job.Status = DocumentJobStatus.Running;
        job.StartedAt = started;

        await _notifier.NotifyAsync(job);

        try
        {
            job.ResultJson = job.Kind switch
            {
                DocumentJobKind.Extract => await RunExtractAsync(job),
                DocumentJobKind.Discover => await RunDiscoverAsync(job),
                _ => throw new InvalidOperationException($"Bilinmeyen iş türü: {job.Kind}")
            };

            job.Status = DocumentJobStatus.Succeeded;

            // Hata alanı da TEMİZLENİYOR. EF yalnız değişen kolonları yazdığı için,
            // bakım işinin daha önce bastığı "İşlem yarıda kaldı" metni yerinde kalıyordu:
            // iş gerçekten geç bitince kullanıcı ekranda "başarılı" durumda, altında
            // "işlem yarıda kaldı" yazan bir kart görüyor ve hangisine güveneceğini
            // bilemiyordu.
            job.Error = null;
        }
        catch (InvalidQueryException ex)
        {
            // Kullanıcının düzeltebileceği durumlar: belge okunamadı, şema tanımsız.
            job.Status = DocumentJobStatus.Failed;
            job.Error = ex.Message;
        }
        catch (QueryPlannerException ex)
        {
            // Model servisi kapalı ya da yanıt vermiyor.
            job.Status = DocumentJobStatus.Failed;
            job.Error = ex.Message;
        }
        catch (Exception ex)
        {
            // Beklenmeyen hata: kullanıcıya iç ayrıntı gösterilmez, günlüğe tam hâli düşer.
            _logger.LogError(ex, "Belge işi beklenmedik biçimde düştü: {JobId}", jobId);
            job.Status = DocumentJobStatus.Failed;
            job.Error = "Belge işlenirken beklenmeyen bir hata oluştu.";
        }

        job.CompletedAt = DateTime.UtcNow;

        // Görüntü BAŞARISIZ işte hemen silinmiyor: kullanıcı onay ekranında "hangi belgeyi
        // yüklemiştim" diye bakabilmeli. Temizlik işi süresi dolanları topluyor.
        //
        // KAYIT SİLİNMİŞ OLABİLİR. Varlık, 30-300 saniye süren model çağrısı boyunca
        // izlenir hâlde duruyor; bu arada kullanıcı işi atabilir ("At" düğmesi), ya da
        // veri seti/kullanıcı silinip cascade ile iş kaydı gidebilir. EF o zaman "1 satır
        // beklenirken 0 etkilendi" diyerek DbUpdateConcurrencyException fırlatıyordu ve
        // bu istisna RunAsync'ten dışarı çıkıyordu: AutomaticRetry kapalı olduğu için iş
        // Hangfire'da kalıcı "Failed" olarak birikiyor, günlüğe kullanıcı hatası değil bir
        // çökme olarak düşüyordu. Silme, işin bilinçli olarak desteklenen bir sonucu —
        // sessizce sonlanmalı.
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogInformation(
                "Belge işi {JobId} çalışırken silinmiş; sonuç yazılmadı.", jobId);
            return;
        }

        await _notifier.NotifyAsync(job);
    }

    /// Şemalı geçiş: hedef set biliniyor, model o setin kolonlarını arıyor.
    private async Task<string> RunExtractAsync(DocumentJob job)
    {
        if (job.DatasetId is not { } datasetId)
            throw new InvalidOperationException("Çıkarım işinde hedef veri seti yok.");

        var schema = await LoadSchemaAsync(datasetId);

        if (schema.Count == 0)
            throw new InvalidQueryException(
                "Bu veri setinin şeması tanımlı değil; belge hangi alanlara yazılacağını bilemez.");

        await using var stream = new MemoryStream(job.Image ?? Array.Empty<byte>());
        var result = await _vision.ExtractAsync(stream, schema);

        // Hücreler şemaya uyuyor mu? Satırlar ELENMEZ — onay ekranı yanlış hücreyi
        // işaretleyip kullanıcıya düzelttirecek. Buradaki tek amaç neyin uymadığını söylemek.
        var validation = _importService.ValidateRows(result.Table, schema);

        // Kolon eşlemesi ŞEMALI geçişte de kuruluyor.
        //
        // Neden gerekli: hedef şemayı vermek modelin ona uyacağını garanti etmiyor. Ölçülen
        // bir örnekte model "urun_adi" yerine "ürün / hizmet" yazdı; ValidateRows ad üzerinden
        // çalıştığı için o kolon sessizce düştü, ürün adları kaybolarak dört satır kaydedildi
        // ve kullanıcı hiçbir hata görmedi. Eşleme artık kullanıcıya gösteriliyor.
        var datasetName = await _db.Datasets
            .Where(d => d.Id == datasetId)
            .Select(d => d.Name)
            .FirstOrDefaultAsync() ?? string.Empty;

        var alignment = DocumentAlignment.From(
            new DatasetSchema(datasetId, datasetName, schema),
            _importService.DetectSchema(result.Table));

        return Serialize(new DocumentExtractionResponse(
            datasetId,
            result.Table.Headers,
            result.Table.Rows,
            validation.Errors,
            result.Warnings,
            result.Suspect,
            result.Model,
            result.PromptTokens,
            result.NumCtx,
            result.LongEdge,
            result.Attempts,
            result.DurationMs,
            alignment));
    }

    /// Keşif geçişi: şema yok, kolonları model çıkarıyor, sonra var olan setlerle eşleşiyor.
    private async Task<string> RunDiscoverAsync(DocumentJob job)
    {
        await using var stream = new MemoryStream(job.Image ?? Array.Empty<byte>());
        var result = await _vision.DiscoverAsync(stream);

        // Kolon adları modelden, TİPLER değerlerden geliyor — CSV/Excel ile aynı katman.
        var columns = _importService.DetectSchema(result.Table);

        var candidates = await _db.Datasets
            .OrderByDescending(d => d.UpdatedAt ?? d.CreatedAt)
            .Take(MaxCandidateDatasets)
            .Select(d => new DatasetSchema(
                d.Id,
                d.Name,
                _db.DatasetColumns
                    .Where(c => c.DatasetId == d.Id)
                    .OrderBy(c => c.Ordinal)
                    .Select(c => new ColumnSchema(c.Name, c.Type))
                    .ToList()))
            .ToListAsync();

        var matches = SchemaMatcher.Match(columns, candidates);

        return Serialize(new DocumentDiscoveryResponse(
            result.Document.DocumentType,
            columns,
            result.Table.Rows,
            matches.Select(m => new DatasetMatchDto(
                m.DatasetId, m.Name, m.Score, m.Mappings, m.MissingColumns, m.ExtraColumns)).ToList(),
            SuggestName(result.Document.DocumentType),
            result.Warnings,
            result.Suspect,
            result.Model,
            result.PromptTokens,
            result.NumCtx,
            result.LongEdge,
            result.Attempts,
            result.DurationMs));
    }

    // Yeni set açılacaksa ad önerisi. Çoğul yapmaya çalışılmıyor: Türkçede ekin ünlü
    // uyumuna göre değişmesi ("fatura" → "faturalar", "fiş" → "fişler") bu ekranda
    // çözülecek bir sorun değil; kullanıcı adı zaten düzenleyebiliyor.
    private static string SuggestName(string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType)) return "Belgeden gelen veriler";

        var text = documentType.Trim();
        // "fiş" gibi adlarda ilk harfin doğru büyümesi için Türkçe kültür (i → İ).
        return char.ToUpper(text[0], TurkishCulture) + text[1..];
    }

    private Task<List<ColumnSchema>> LoadSchemaAsync(Guid datasetId) =>
        _db.DatasetColumns
            .Where(c => c.DatasetId == datasetId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnSchema(c.Name, c.Type))
            .ToListAsync();

    // Sonuç, istemciye gideceği biçimde saklanıyor (camelCase): okuma ucu onu yeniden
    // biçimlendirmeden geçiriyor, yani aynı gövde iki kez tarif edilmiyor. "Web"
    // varsayılanları ASP.NET Core'un uçlarda kullandığı ayarların aynısı.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions);
}
