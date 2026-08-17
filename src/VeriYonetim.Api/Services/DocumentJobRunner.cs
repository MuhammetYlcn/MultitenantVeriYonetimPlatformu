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
    [AutomaticRetry(Attempts = 0)]
    Task RunAsync(Guid jobId);
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
    // Keşifte karşılaştırılacak en fazla set sayısı: eşleme ucuz ama sorgu sınırsız
    // büyümemeli. En son dokunulanlar en olası adaylar.
    private const int MaxCandidateDatasets = 25;

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
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job is null)
        {
            // Kullanıcı beklerken seti ya da hesabı silinmiş olabilir; iş de onunla gitmiştir.
            _logger.LogWarning("Belge işi bulunamadı, atlanıyor: {JobId}", jobId);
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

        job.Status = DocumentJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
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
        await _db.SaveChangesAsync();
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
            result.DurationMs));
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
