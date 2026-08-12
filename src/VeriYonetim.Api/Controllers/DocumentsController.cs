using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
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
[ApiController]
[Route("api/datasets/{datasetId:guid}/document")]
[Authorize(Policy = AuthPolicies.TenantUser)]
public class DocumentsController : ControllerBase
{
    // Belge görüntüleri fotoğraf olduğu için CSV'den büyük olabilir.
    private const long MaxUploadBytes = 15 * 1024 * 1024; // 15 MB

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

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
    [HttpPost("extract")]
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
