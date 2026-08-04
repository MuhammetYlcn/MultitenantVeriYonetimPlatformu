using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

// Doğal dilde sorgu.
//
// Uç veri setine bağlı DEĞİL: model firmanın bütün kataloğunu (setler, kolonlar, aralarındaki
// ilişkiler) görür ve hangisini kullanacağına kendisi karar verir.
//
// Model SQL yazmaz, PLAN üretir; SQL'i doğrulanmış builder yazar. Bunun izolasyon açısından
// önemi şu: modelin ürettiği hiçbir karakter SQL metnine girmez ve sorguya katılan her veri
// seti kataloğa girerken zaten tenant filtresinden geçmiştir — yani model uydursa bile
// başka bir firmanın setine erişemez.
[ApiController]
[Authorize(Policy = AuthPolicies.TenantUser)]
public class AskController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IQueryPlannerService _planner;
    private readonly IDatasetQueryExecutor _executor;
    private readonly ILogger<AskController> _logger;

    public AskController(AppDbContext db, IQueryPlannerService planner,
        IDatasetQueryExecutor executor, ILogger<AskController> logger)
    {
        _db = db;
        _planner = planner;
        _executor = executor;
        _logger = logger;
    }

    // POST /api/ask — soru sor. Viewer da çağırabilir: sorgu salt okuma bir işlemdir.
    [HttpPost("api/ask")]
    public async Task<IActionResult> Ask(AskRequest request, CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync();

        if (catalog.Datasets.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu firmada henüz veri seti yok.");

        PlanResult planResult;
        try
        {
            planResult = await _planner.PlanAsync(request.Question, catalog, ct);
        }
        catch (InvalidQueryException ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
        catch (QueryPlannerException ex)
        {
            // Altyapı sorunu: kullanıcının sorusunu değiştirerek çözebileceği bir şey yok.
            _logger.LogWarning(ex, "Sorgu planlayıcı çalışmadı.");
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: ex.Message);
        }

        try
        {
            return await ExecuteAsync(request.Question, planResult, catalog, ct);
        }
        catch (InvalidQueryException ex)
        {
            // Model plan üretti ama plan tutarsız. Kullanıcı soruyu değiştirerek çözebilir.
            _logger.LogInformation("Geçersiz plan: {Message}. Soru: {Question}. Plan: {Plan}",
                ex.Message, request.Question, planResult.RawJson);

            return Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
    }

    private async Task<IActionResult> ExecuteAsync(
        string question, PlanResult planResult, TenantCatalog catalog, CancellationToken ct)
    {
        var plan = planResult.Plan;
        var kind = QueryPlanMapper.NormalizeKind(plan);
        var datasetNames = QueryPlanMapper.DatasetNames(plan);

        if (kind == "unsupported")
        {
            // Cevaplanamayan sorular KAYDA GEÇER. Sırada hangi yeteneğin eksik olduğu
            // tahminle değil gerçek kullanımla belirlensin diye.
            _logger.LogInformation("CEVAPLANAMADI. Soru: {Question}. Gerekçe: {Reason}",
                question, plan.Reason);

            return Ok(new AskResponse(
                Question: question,
                Kind: kind,
                Summary: PlanSummary.Describe(plan, datasetNames),
                Datasets: Array.Empty<string>(),
                Reason: plan.Reason ?? "Bu soru mevcut verilerle cevaplanamıyor.",
                PlanMs: planResult.DurationMs));
        }

        var scope = catalog.BuildScope(datasetNames);
        var used = scope.Sources.Select(s => s.Name).ToList();
        var summary = PlanSummary.Describe(plan, used);

        var stopwatch = Stopwatch.StartNew();

        if (kind == "rows")
        {
            var built = DatasetRowQueryBuilder.BuildSelect(QueryPlanMapper.ToRowQuery(plan), scope);
            var rows = await _executor.RunRowsAsync(built, ct);
            stopwatch.Stop();

            return Ok(new AskResponse(
                question, kind, summary, used,
                Sql: built.Sql,
                PlanMs: planResult.DurationMs,
                QueryMs: (int)stopwatch.ElapsedMilliseconds,
                Rows: new AskRowsResult(built.Columns, rows)));
        }

        if (plan.Compare is not null)
            return await CompareAsync(question, planResult, plan, scope, used, summary, stopwatch, ct);

        var aggregateQuery = QueryPlanMapper.ToAggregateQuery(plan);
        var aggregate = DatasetAggregateQueryBuilder.Build(aggregateQuery, scope);
        var buckets = await _executor.RunAggregateAsync(aggregate, ct: ct);
        stopwatch.Stop();

        return Ok(new AskResponse(
            question, kind, summary, used,
            Sql: aggregate.Sql,
            PlanMs: planResult.DurationMs,
            QueryMs: (int)stopwatch.ElapsedMilliseconds,
            Aggregate: new AskAggregateResult(
                aggregateQuery.GroupBy, aggregateQuery.Metrics, aggregateQuery.Bucket, buckets)));
    }

    // "Bu ay geçen aya göre nasıl?" — tek SQL ile çözülmez: aynı agregasyon iki dönem için
    // çalıştırılıp sonuçlar eşleştirilir. Limit iki sorgudan da çıkarılır, eşleştirmeden
    // SONRA uygulanır (bkz. QueryPlanMapper.ToComparisonQueries).
    private async Task<IActionResult> CompareAsync(
        string question, PlanResult planResult, QueryPlan plan, QueryScope scope,
        IReadOnlyList<string> used, string summary, Stopwatch stopwatch, CancellationToken ct)
    {
        var (currentQuery, previousQuery) = QueryPlanMapper.ToComparisonQueries(plan);

        var currentBuilt = DatasetAggregateQueryBuilder.Build(currentQuery, scope);
        var previousBuilt = DatasetAggregateQueryBuilder.Build(previousQuery, scope);

        var currentBuckets = await _executor.RunAggregateAsync(currentBuilt, ct: ct);
        var previousBuckets = await _executor.RunAggregateAsync(previousBuilt, ct: ct);

        var merged = PeriodComparison.Merge(
            currentBuckets.Select(b => (b.Key, b.Value)).ToList(),
            previousBuckets.Select(b => (b.Key, b.Value)).ToList());

        // Limit burada uygulanır: mevcut döneme göre büyükten küçüğe.
        if (plan.Limit is int limit && limit > 0)
            merged = merged.OrderByDescending(b => b.Current ?? decimal.MinValue).Take(limit).ToList();

        stopwatch.Stop();

        return Ok(new AskResponse(
            question, "aggregate", summary, used,
            // İki sorgu çalıştı; kullanıcıya ikisini de gösteriyoruz ki karşılaştırmanın
            // nasıl kurulduğu görünür olsun.
            Sql: $"-- Mevcut dönem\n{currentBuilt.Sql}\n\n-- Önceki dönem\n{previousBuilt.Sql}",
            PlanMs: planResult.DurationMs,
            QueryMs: (int)stopwatch.ElapsedMilliseconds,
            Comparison: new AskComparisonResult(
                plan.Compare!.Period ?? "", plan.Compare.Previous ?? "", merged)));
    }

    // Firmanın kataloğu. Global query filter sayesinde yalnız bu tenant'ın setleri gelir —
    // izolasyonun dayanağı bu: kataloğa girmeyen bir veri seti sorguya da giremez.
    private async Task<TenantCatalog> LoadCatalogAsync()
    {
        var datasets = await _db.Datasets
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.Description })
            .ToListAsync();

        var columns = await _db.DatasetColumns
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.DatasetId, c.Name, c.Type })
            .ToListAsync();

        var byDataset = columns
            .GroupBy(c => c.DatasetId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)
                g.ToDictionary(c => c.Name, c => c.Type));

        var infos = datasets
            .Select(d => new DatasetInfo(d.Id, d.Name, d.Description,
                byDataset.GetValueOrDefault(d.Id, new Dictionary<string, string>())))
            // Şemasız setler modele hiç gösterilmez: seçilirlerse sorgu kurulamaz ve
            // kullanıcı sebebini anlamayacağı bir hata alırdı.
            .Where(d => d.Columns.Count > 0)
            .ToList();

        var relations = await _db.DatasetRelations
            .Select(r => new RelationInfo(r.FromDatasetId, r.FromColumn, r.ToDatasetId, r.ToColumn))
            .ToListAsync();

        return new TenantCatalog(infos, relations);
    }
}
