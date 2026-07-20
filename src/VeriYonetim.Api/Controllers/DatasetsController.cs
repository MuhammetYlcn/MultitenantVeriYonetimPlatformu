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

    public DatasetsController(AppDbContext db, ITenantContext tenantContext)
    {
        _db = db;
        _tenantContext = tenantContext;
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

    // Entity → dışa açık DTO. Tek yerde map: her endpoint aynı alanları döner.
    private static DatasetResponse ToResponse(Dataset d) =>
        new(d.Id, d.Name, d.Description, d.RowCount, d.CreatedAt, d.UpdatedAt);

    // Tutarlı 404: elle {message} obje yerine standart ProblemDetails üretir.
    // Not: çapraz-tenant erişim de buraya düşer; "yetkiniz yok" demeyip "bulunamadı"
    // diyerek kaydın varlığını sızdırmıyoruz (enumeration önlemi).
    private ObjectResult DatasetNotFound() =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Veri seti bulunamadı.");
}
