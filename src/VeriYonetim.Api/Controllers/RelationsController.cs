using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

// Veri setleri arasındaki ilişkiler ("Satışlar.musteri_no ↔ Müşteriler.no").
//
// Uç veri setine değil FİRMAYA bağlı: bir ilişki iki sete birden aittir, birinin altında
// durması yanıltıcı olurdu. Sınıf düzeyindeki politika DatasetsController ile aynı —
// platform yöneticisi buraya da giremez.
[ApiController]
[Route("api/relations")]
[Authorize(Policy = AuthPolicies.TenantUser)]
public class RelationsController : ControllerBase
{
    private readonly AppDbContext _db;

    public RelationsController(AppDbContext db) => _db = db;

    // GET /api/relations — firmanın tanımlı tüm ilişkileri.
    [HttpGet]
    public async Task<IActionResult> GetRelations()
    {
        var relations = await _db.DatasetRelations
            .OrderBy(r => r.CreatedAt)
            .Select(r => new RelationResponse(
                r.Id,
                r.FromDatasetId, r.FromDataset.Name, r.FromColumn,
                r.ToDatasetId, r.ToDataset.Name, r.ToColumn,
                r.IsAutoDetected))
            .ToListAsync();

        return Ok(relations);
    }

    // POST /api/relations — yeni ilişki tanımla.
    [HttpPost]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> CreateRelation(CreateRelationRequest request)
    {
        if (request.FromDatasetId == request.ToDatasetId)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bir veri seti kendisiyle ilişkilendirilemez.");

        // Query filter sayesinde başka firmanın seti burada zaten bulunamaz.
        var from = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == request.FromDatasetId);
        var to = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == request.ToDatasetId);

        if (from is null || to is null)
            return Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Veri seti bulunamadı.");

        if (await ColumnMissingAsync(from, request.FromColumn) is { } fromError) return fromError;
        if (await ColumnMissingAsync(to, request.ToColumn) is { } toError) return toError;

        // Aynı bağ iki kez kurulmasın — hangi yönden yazıldığından bağımsız olarak.
        var exists = await _db.DatasetRelations.AnyAsync(r =>
            (r.FromDatasetId == request.FromDatasetId && r.FromColumn == request.FromColumn &&
             r.ToDatasetId == request.ToDatasetId && r.ToColumn == request.ToColumn) ||
            (r.FromDatasetId == request.ToDatasetId && r.FromColumn == request.ToColumn &&
             r.ToDatasetId == request.FromDatasetId && r.ToColumn == request.FromColumn));

        if (exists)
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Bu iki kolon arasında zaten bir ilişki tanımlı.");

        var relation = new DatasetRelation
        {
            Id = Guid.NewGuid(),
            FromDatasetId = request.FromDatasetId,
            FromColumn = request.FromColumn,
            ToDatasetId = request.ToDatasetId,
            ToColumn = request.ToColumn,
            // Elle tanımlandı: algılama bu kayda dokunmaz, kullanıcının kararı esastır.
            IsAutoDetected = false
        };

        _db.DatasetRelations.Add(relation);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetRelations), new { id = relation.Id },
            new RelationResponse(relation.Id,
                from.Id, from.Name, relation.FromColumn,
                to.Id, to.Name, relation.ToColumn, relation.IsAutoDetected));
    }

    // DELETE /api/relations/{id}
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> DeleteRelation(Guid id)
    {
        var relation = await _db.DatasetRelations.FirstOrDefaultAsync(r => r.Id == id);
        if (relation is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "İlişki bulunamadı.");

        _db.DatasetRelations.Remove(relation);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    // Kolon o veri setinin kayıtlı şemasında yoksa 400 döner; varsa null.
    private async Task<IActionResult?> ColumnMissingAsync(Dataset dataset, string column)
    {
        var exists = await _db.DatasetColumns
            .AnyAsync(c => c.DatasetId == dataset.Id && c.Name == column);

        return exists
            ? null
            : Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"'{dataset.Name}' veri setinde '{column}' diye bir kolon yok.");
    }
}
