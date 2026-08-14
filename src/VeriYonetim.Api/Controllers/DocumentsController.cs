using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

/// <summary>
/// Belge (fatura, fiş, makbuz) yükleme — veri girişinin ÜÇÜNCÜ kapısı.
///
/// Ana karar: belgeden çıkan şey yapılandırılmış bir tablodur; oradan sonrası zaten var
/// olan yoldur (tip algılama → şema → JSONB satır). Ayrı bir "Fatura" tablosu AÇILMIYOR:
/// yüklenen belge her zaman fatura olmayabilir, üstelik ikinci bir veri modeli panoyu ve
/// doğal dilde sorguyu ayrıca bağlamayı gerektirirdi.
///
/// Sınıf düzeyindeki politika DatasetsController ile aynı — platform yöneticisi buraya da
/// giremez. Yazma yetkisi Editor/Admin: belge yüklemek veri girişidir.
/// </summary>
/// Yol şablonu eylem başına yazılıyor çünkü iki uç iki farklı düzeyde duruyor: çıkarım bir
/// veri setinin altında (hedef bellidir), keşif firma düzeyinde (hedef henüz yoktur).
[ApiController]
[Route("api")]
[Authorize(Policy = AuthPolicies.TenantUser)]
public class DocumentsController : ControllerBase
{
    // Belge görüntüleri fotoğraf olduğu için CSV'den büyük olabilir.
    private const long MaxUploadBytes = 15 * 1024 * 1024; // 15 MB

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    // Keşifte karşılaştırılacak en fazla set sayısı.
    private const int MaxCandidateDatasets = 25;

    // Tek belgeden kaydedilebilecek en fazla satır. Bir fatura onlarca kalem taşır, yüzlerce
    // değil; bu sınır kötü niyetli ya da bozuk bir isteğin veritabanını şişirmesini engeller.
    private const int MaxConfirmRows = 500;

    // "fiş" gibi adlarda ilk harfin doğru büyütülmesi için (i → İ).
    private static readonly CultureInfo TurkishCulture = new("tr-TR");

    private readonly AppDbContext _db;
    private readonly IDocumentVisionService _vision;
    private readonly IDatasetImportService _importService;

    public DocumentsController(AppDbContext db, IDocumentVisionService vision,
        IDatasetImportService importService)
    {
        _db = db;
        _vision = vision;
        _importService = importService;
    }

    /// <summary>
    /// Belgeyi okur ve çıkan tabloyu ÖNİZLEME olarak döndürür — hiçbir şey kaydedilmez.
    /// Kaydetme, onay ekranından gelecek ayrı bir çağrıyla yapılacak.
    /// </summary>
    [HttpPost("datasets/{datasetId:guid}/document/extract")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Extract(Guid datasetId, IFormFile? file,
        CancellationToken ct)
    {
        // Sahiplik doğrulaması: global query filter sayesinde başka firmanın seti burada
        // zaten bulunamaz. 403 değil 404 dönüyoruz (kaydın varlığını sızdırmamak için).
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == datasetId, ct);
        if (dataset is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Veri seti bulunamadı.");

        if (ValidateUpload(file) is { } error) return error;

        // Hedef şema ZORUNLU: model neyi arayacağını bilmeden serbest çıkarım yapar ve alan
        // adlarını uydurur — çıkan sonuç hiçbir veri setine yazılamaz (bkz. DocumentPromptBuilder).
        var schema = await _db.DatasetColumns
            .Where(c => c.DatasetId == datasetId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnSchema(c.Name, c.Type))
            .ToListAsync(ct);

        if (schema.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu veri seti için önce şema tanımlayın (POST /api/datasets/{id}/schema).");

        DocumentExtractionResult result;
        try
        {
            await using var stream = file!.OpenReadStream();
            result = await _vision.ExtractAsync(stream, schema, ct);
        }
        catch (InvalidQueryException ex)
        {
            // Belge okunamadı / şema yok gibi kullanıcının düzeltebileceği durumlar.
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
        catch (QueryPlannerException ex)
        {
            // Model servisi kapalı ya da yanıt vermiyor: kullanıcının yapabileceği bir şey yok.
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: ex.Message);
        }

        // Hücreler şemaya uyuyor mu? Satırlar ELENMEZ — onay ekranı yanlış hücreyi
        // işaretleyip kullanıcıya düzelttirecek. Buradaki tek amaç neyin uymadığını söylemek.
        var validation = _importService.ValidateRows(result.Table, schema);

        return Ok(new DocumentExtractionResponse(
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

    /// <summary>
    /// Onay ekranındaki tabloyu veri setine EKLER — belgeden veri girişinin son adımı.
    ///
    /// İçe aktarmadan (`POST /rows`) farkı bilinçli: o "değiştir" semantiğiyle çalışır,
    /// çünkü bir dosya setin tamamıdır. Belge ise bir KAYITTIR; her fatura eskilerin
    /// üstüne yazsaydı ikinci belge birinciyi silerdi.
    ///
    /// Ya hep ya hiç: tek bir hücre bile şemaya uymuyorsa hiçbir satır yazılmaz ve hangi
    /// hücrenin uymadığı geri döner. Yarısı yazılmış bir belge, kullanıcının hangi satırın
    /// içeride olduğunu bilemediği bir durum bırakırdı.
    /// </summary>
    [HttpPost("datasets/{datasetId:guid}/document/confirm")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Confirm(Guid datasetId, DocumentConfirmRequest request,
        CancellationToken ct)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == datasetId, ct);
        if (dataset is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Veri seti bulunamadı.");

        var columns = request.Columns ?? Array.Empty<string>();
        var rows = request.Rows ?? Array.Empty<string[]>();

        if (columns.Count == 0 || rows.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Kaydedilecek satır yok.");

        if (rows.Count > MaxConfirmRows)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Tek belgeden en fazla {MaxConfirmRows} satır kaydedilebilir.");

        // Satır uzunluğu başlık sayısıyla uyuşmuyorsa hücreler kayar ve veri SESSİZCE
        // yanlış kolona yazılır — bu yüzden kırpmak/tamamlamak yerine reddediyoruz.
        if (rows.Any(r => r.Length != columns.Count))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Satırlardaki hücre sayısı kolon sayısıyla uyuşmuyor.");

        var schema = await _db.DatasetColumns
            .Where(c => c.DatasetId == datasetId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnSchema(c.Name, c.Type))
            .ToListAsync(ct);

        if (schema.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu veri seti için önce şema tanımlayın (POST /api/datasets/{id}/schema).");

        // Doğrulama içe aktarmayla BİREBİR aynı katmandan geçiyor: belgeden gelen satır ile
        // CSV'den gelen satır aynı kurallara tabi, yoksa iki kapı iki farklı veri üretirdi.
        var validation = _importService.ValidateRows(new ParsedTable(columns, rows), schema);

        if (validation.Errors.Count > 0)
            return ValidationProblem(new ValidationProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bazı hücreler şemaya uymuyor; düzeltip tekrar deneyin.",
                Extensions = { ["cells"] = validation.Errors }
            });

        foreach (var values in validation.ValidRows)
            _db.DatasetRows.Add(new DatasetRow
            {
                Id = Guid.NewGuid(),
                DatasetId = datasetId,
                Data = values
            });

        dataset.RowCount += validation.ValidRows.Count;
        dataset.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new DocumentConfirmResponse(datasetId, validation.ValidRows.Count,
            dataset.RowCount));
    }

    /// <summary>
    /// Belgeyi ŞEMASIZ okur (keşif geçişi) ve hangi veri setine ait olabileceğini önerir.
    /// Hiçbir şey kaydedilmez; set seçimi ve kaydetme onay ekranından gelecek.
    ///
    /// Bu uç neden var: `extract` hedef seti bilmek zorunda. İlk kez görülen bir belge
    /// türünde böyle bir set yoktur — kullanıcıya "önce elle set açın, kolonlarını yazın,
    /// sonra belgeyi yükleyin" demek, belgeden veri çıkarmanın bütün anlamını götürürdü.
    /// </summary>
    [HttpPost("documents/discover")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Discover(IFormFile? file, CancellationToken ct)
    {
        if (ValidateUpload(file) is { } error) return error;

        DocumentExtractionResult result;
        try
        {
            await using var stream = file!.OpenReadStream();
            result = await _vision.DiscoverAsync(stream, ct);
        }
        catch (InvalidQueryException ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
        catch (QueryPlannerException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: ex.Message);
        }

        // Kolon adları modelden, TİPLER değerlerden geliyor. Tipi de modele sordurmak
        // ikinci bir tip algılayıcı demek olurdu; CSV/Excel ile aynı katmandan geçmesi
        // hem tutarlılığı hem "1.500" gibi tuzakların tek yerde çözülmesini sağlıyor.
        var columns = _importService.DetectSchema(result.Table);

        // Adaylar: firmanın var olan setleri (global filtre başka firmanınkini zaten
        // getirmez). Sayı sınırlı — eşleme tarafı ucuz ama sorgu sınırsız büyümemeli;
        // en son dokunulanlar en olası adaylar.
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
            .ToListAsync(ct);

        var matches = SchemaMatcher.Match(columns, candidates);

        return Ok(new DocumentDiscoveryResponse(
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
        return char.ToUpper(text[0], TurkishCulture) + text[1..];
    }

    private IActionResult? ValidateUpload(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya boş veya gönderilmedi.");

        if (file.Length > MaxUploadBytes)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya 15 MB sınırını aşıyor.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Yalnızca .jpg, .jpeg, .png ve .webp görüntüleri desteklenir.");

        return null;
    }
}
