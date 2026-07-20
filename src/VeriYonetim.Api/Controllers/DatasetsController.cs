using CsvHelper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

[ApiController]
[Route("api/datasets")]
[Authorize]
public class DatasetsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IDatasetImportService _importService;

    public DatasetsController(AppDbContext db, ITenantContext tenantContext,
        IDatasetImportService importService)
    {
        _db = db;
        _tenantContext = tenantContext;
        _importService = importService;
    }

    // GET /api/datasets — tenant'ın tüm setleri. Where yok: izolasyonu query filter sağlar.
    [HttpGet]
    public async Task<IActionResult> GetDatasets()
    {
        var datasets = await _db.Datasets
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => ToResponse(d))
            .ToListAsync();

        return Ok(datasets);
    }

    // GET /api/datasets/{id} — tek set.
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDataset(Guid id)
    {
        // Dikkat: FindAsync DEĞİL. FindAsync global query filter'ı atlar; başka
        // tenant'ın kaydını id ile getirebilirdi. FirstOrDefaultAsync filtreyi uygular.
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == id);
        if (dataset is null)
            return DatasetNotFound();

        return Ok(ToResponse(dataset));
    }

    // POST /api/datasets — yeni set. TenantId istekten değil token'dan gelir.
    [HttpPost]
    public async Task<IActionResult> CreateDataset(CreateDatasetRequest request)
    {
        var dataset = new Dataset
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.TenantId!.Value,
            Name = request.Name,
            Description = request.Description
        };

        _db.Datasets.Add(dataset);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetDataset), new { id = dataset.Id }, ToResponse(dataset));
    }

    // POST /api/datasets/analyze — CSV/Excel yükle; kolonları ve tipleri algılayıp
    // önizleme döndür (kalıcı kayıt YOK). Dosya multipart/form-data ile gelir.
    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeFile(IFormFile? file)
    {
        if (ValidateUpload(file, out var ext) is { } error) return error;

        var table = await TryParseAsync(file!, ext);
        if (table is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya okunamadı; biçimi geçersiz olabilir.");

        var columns = _importService.DetectSchema(table);

        return Ok(new { fileName = file!.FileName, rowCount = table.Rows.Count, columns });
    }

    // POST /api/datasets/{id}/schema — dosyayı analiz et ve algılanan kolonları bu sete
    // KALICI kaydet (var olan kolonları değiştirir). Dün'ün analyze'ı artık kaydediyor.
    [HttpPost("{id:guid}/schema")]
    public async Task<IActionResult> SetSchema(Guid id, IFormFile? file)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == id);
        if (dataset is null) return DatasetNotFound();

        if (ValidateUpload(file, out var ext) is { } error) return error;

        var table = await TryParseAsync(file!, ext);
        if (table is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya okunamadı; biçimi geçersiz olabilir.");

        var detected = _importService.DetectSchema(table);

        // Yeniden yükleme senaryosu: eski kolonları sil, yenilerini sırayla ekle.
        var existing = await _db.DatasetColumns.Where(c => c.DatasetId == id).ToListAsync();
        _db.DatasetColumns.RemoveRange(existing);

        var ordinal = 0;
        foreach (var col in detected)
            _db.DatasetColumns.Add(new DatasetColumn
            {
                Id = Guid.NewGuid(),
                DatasetId = id,
                Name = col.Name,
                Type = col.Type,
                Ordinal = ordinal++
            });

        dataset.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { datasetId = id, columns = detected });
    }

    // GET /api/datasets/{id}/schema — kaydedilmiş kolon tanımlarını döndür.
    [HttpGet("{id:guid}/schema")]
    public async Task<IActionResult> GetSchema(Guid id)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == id);
        if (dataset is null) return DatasetNotFound();

        var columns = await _db.DatasetColumns
            .Where(c => c.DatasetId == id)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.Name, c.Type, c.Ordinal })
            .ToListAsync();

        return Ok(new { datasetId = id, columns });
    }

    // PUT /api/datasets/{id} — güncelle.
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateDataset(Guid id, UpdateDatasetRequest request)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == id);
        if (dataset is null)
            return DatasetNotFound();

        dataset.Name = request.Name;
        dataset.Description = request.Description;
        dataset.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(ToResponse(dataset));
    }

    // DELETE /api/datasets/{id} — sil.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteDataset(Guid id)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == id);
        if (dataset is null)
            return DatasetNotFound();

        _db.Datasets.Remove(dataset);
        await _db.SaveChangesAsync();

        return NoContent(); // 204 — silindi, dönecek gövde yok.
    }

    private const long MaxUploadBytes = 10 * 1024 * 1024; // 10 MB

    // Yükleme doğrulaması. Geçerliyse null döner + ext'i doldurur; değilse 400 döner.
    private IActionResult? ValidateUpload(IFormFile? file, out string ext)
    {
        ext = "";
        if (file is null || file.Length == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya boş veya gönderilmedi.");
        if (file.Length > MaxUploadBytes)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya 10 MB sınırını aşıyor.");
        ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx"))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Yalnızca .csv ve .xlsx dosyaları desteklenir.");
        return null;
    }

    // Dosyayı uzantısına göre ayrıştırır. Bozuk/geçersiz dosyada null döner.
    private async Task<ParsedTable?> TryParseAsync(IFormFile file, string ext)
    {
        try
        {
            await using var stream = file.OpenReadStream();
            return ext == ".csv"
                ? await _importService.ParseCsvAsync(stream)
                : await _importService.ParseExcelAsync(stream);
        }
        catch (Exception ex) when (ex is CsvHelperException or InvalidDataException)
        {
            return null;
        }
    }

    // Entity → dışa açık DTO. Tek yerde map: her endpoint aynı alanları döner.
    private static DatasetResponse ToResponse(Dataset d) =>
        new(d.Id, d.Name, d.Description, d.RowCount, d.CreatedAt, d.UpdatedAt);

    // Tutarlı 404: elle {message} obje yerine standart ProblemDetails üretir.
    // Not: çapraz-tenant erişim de buraya düşer; "yetkiniz yok" demeyip "bulunamadı"
    // diyerek kaydın varlığını sızdırmıyoruz (enumeration önlemi).
    private ObjectResult DatasetNotFound() =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Veri seti bulunamadı.");
}
