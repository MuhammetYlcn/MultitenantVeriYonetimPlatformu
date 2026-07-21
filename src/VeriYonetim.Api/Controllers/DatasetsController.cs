using CsvHelper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

    // POST /api/datasets/{id}/rows — dosyadaki satırları kayıtlı şemaya göre doğrula ve
    // geçerlileri JSONB olarak içeri al. Değiştir semantiği: her import eski satırların
    // yerine geçer. Geçersiz satırlar elenir ve hata raporunda döner.
    [HttpPost("{id:guid}/rows")]
    public async Task<IActionResult> ImportRows(Guid id, IFormFile? file)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == id);
        if (dataset is null) return DatasetNotFound();

        if (ValidateUpload(file, out var ext) is { } error) return error;

        // Validasyonun dayatacağı tip tanımı: kayıtlı şema. Yoksa önce /schema çağrılmalı.
        var schema = await _db.DatasetColumns
            .Where(c => c.DatasetId == id)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnSchema(c.Name, c.Type))
            .ToListAsync();

        if (schema.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu veri seti için önce şema tanımlayın (POST /api/datasets/{id}/schema).");

        var table = await TryParseAsync(file!, ext);
        if (table is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya okunamadı; biçimi geçersiz olabilir.");

        // Başlık uyumu: dosyanın kolonları kayıtlı şemayla aynı kümede olmalı.
        var schemaNames = schema.Select(c => c.Name).ToHashSet();
        var fileNames = table.Headers.ToHashSet();
        if (!schemaNames.SetEquals(fileNames))
        {
            var missing = schemaNames.Except(fileNames).ToList();
            var extra = fileNames.Except(schemaNames).ToList();
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Yüklenen dosyanın kolonları kayıtlı şemayla uyuşmuyor.",
                detail: $"Eksik: [{string.Join(", ", missing)}] | Fazladan: [{string.Join(", ", extra)}]");
        }

        var result = _importService.ValidateRows(table, schema);

        // Değiştir semantiği (SetSchema deseni): eski satırları sil, geçerlileri ekle.
        // Not: çok büyük import'larda ExecuteDeleteAsync + bulk insert daha verimli olurdu.
        var existing = await _db.DatasetRows.Where(r => r.DatasetId == id).ToListAsync();
        _db.DatasetRows.RemoveRange(existing);

        foreach (var rowData in result.ValidRows)
            _db.DatasetRows.Add(new DatasetRow
            {
                Id = Guid.NewGuid(),
                DatasetId = id,
                Data = rowData
            });

        dataset.RowCount = result.ValidRows.Count;
        dataset.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        const int maxErrors = 100; // yanıtı şişirmemek için hata listesini sınırla
        return Ok(new
        {
            datasetId = id,
            totalRows = table.Rows.Count,
            imported = result.ValidRows.Count,
            failed = result.Errors.Count,
            errors = result.Errors.Take(maxErrors),
            errorsTruncated = result.Errors.Count > maxErrors
        });
    }

    // GET /api/datasets/{id}/rows — satırları sayfalayarak, sıralayarak ve JSONB üzerinden
    // filtreleyerek döndür. Filtre formatı: ?filter=kolon:op:deger (birden çok olabilir),
    // op ∈ {eq,ne,gt,gte,lt,lte,contains}. Örn: ?sort=yas&dir=desc&filter=yas:gte:30
    [HttpGet("{id:guid}/rows")]
    public async Task<IActionResult> GetRows(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? sort = null,
        [FromQuery] string? dir = null,
        [FromQuery(Name = "filter")] string[]? filter = null)
    {
        // Sahiplik doğrulaması: tenant-filtreli sorgu. Bu ham SQL'in izolasyon dayanağı —
        // aşağıdaki FromSqlRaw global query filter'ı atlar, o yüzden burada doğrulanmalı.
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == id);
        if (dataset is null) return DatasetNotFound();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Whitelist + tip kaynağı: kayıtlı şema (kolon adı → tip).
        var schema = await _db.DatasetColumns
            .Where(c => c.DatasetId == id)
            .ToDictionaryAsync(c => c.Name, c => c.Type);

        // "kolon:op:deger" ayrıştır. Değerin içinde ':' olabilir (tarih-saat) → en fazla 3 parça.
        var filters = new List<RowFilter>();
        foreach (var raw in filter ?? [])
        {
            var parts = raw.Split(':', 3);
            if (parts.Length != 3)
                return Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: $"Geçersiz filtre biçimi: '{raw}'. Beklenen: kolon:op:deger.");
            filters.Add(new RowFilter(parts[0], parts[1], parts[2]));
        }

        BuiltQuery built;
        try
        {
            built = DatasetRowQueryBuilder.Build(
                new RowQuery(page, pageSize, sort, dir, filters), schema);
        }
        catch (InvalidQueryException ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }

        // Toplam kayıt (sayfa metadata'sı için). Parametreleri her komut için taze üretiyoruz
        // (bir NpgsqlParameter aynı anda iki komuta ait olamaz).
        // COUNT(*) PostgreSQL'de bigint döner; ::int ile int32'ye indiriyoruz (SqlQueryRaw<int>).
        var countSql = $"""SELECT COUNT(*)::int AS "Value" FROM "DatasetRows" WHERE "DatasetId" = @datasetId{built.WhereSql}""";
        var total = (await _db.Database
            .SqlQueryRaw<int>(countSql, Params(id, built).ToArray())
            .ToListAsync())[0];

        // Sayfa verisi. IgnoreQueryFilters: EF ham SQL'i sarmalayıp ORDER BY/LIMIT'i
        // bozmasın diye — izolasyon zaten datasetId + yukarıdaki sahiplik kontrolüyle sağlı.
        var offset = (page - 1) * pageSize;
        var pageSql = $"""
            SELECT "Id", "Data", "DatasetId" FROM "DatasetRows"
            WHERE "DatasetId" = @datasetId{built.WhereSql}{built.OrderBySql}
            LIMIT @limit OFFSET @offset
            """;
        var rowParams = Params(id, built);
        rowParams.Add(new NpgsqlParameter("limit", pageSize));
        rowParams.Add(new NpgsqlParameter("offset", offset));

        var rows = await _db.DatasetRows
            .FromSqlRaw(pageSql, rowParams.ToArray())
            .IgnoreQueryFilters()
            .Select(r => new RowItem(r.Id, r.Data))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return Ok(new RowListResponse(page, pageSize, total, totalPages, rows));
    }

    // datasetId + filtre parametrelerinden taze bir liste üretir (komutlar paylaşamadığı için).
    private static List<NpgsqlParameter> Params(Guid datasetId, BuiltQuery built)
    {
        var list = new List<NpgsqlParameter> { new("datasetId", datasetId) };
        foreach (var p in built.Parameters)
            list.Add(new NpgsqlParameter(p.ParameterName, p.NpgsqlDbType) { Value = p.Value });
        return list;
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
