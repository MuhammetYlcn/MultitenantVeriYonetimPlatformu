using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;
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
    private readonly ITenantContext _tenantContext;
    private readonly ITenantCatalogLoader _catalogLoader;
    private readonly IQueryPlannerService _planner;
    private readonly IDatasetQueryExecutor _executor;
    private readonly IAskSuggestionService _suggestions;
    private readonly ILogger<AskController> _logger;

    public AskController(AppDbContext db, ITenantContext tenantContext,
        ITenantCatalogLoader catalogLoader,
        IQueryPlannerService planner, IDatasetQueryExecutor executor,
        IAskSuggestionService suggestions, ILogger<AskController> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _catalogLoader = catalogLoader;
        _planner = planner;
        _executor = executor;
        _suggestions = suggestions;
        _logger = logger;
    }

    // GET /api/ask/models — kurulu modeller. Arayüzdeki model seçici bunu okur.
    [HttpGet("api/ask/models")]
    public async Task<IActionResult> GetModels(CancellationToken ct)
    {
        try
        {
            return Ok(await _planner.ListModelsAsync(ct: ct));
        }
        catch (QueryPlannerException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: ex.Message);
        }
    }

    // GET /api/ask/suggestions — firmanın kendi verisine göre örnek sorular.
    //
    // ready=false ise üretim arka planda sürüyor demektir; istemci biraz sonra tekrar sorar.
    // Öneriler kullanıcıya gösterilmeden önce gerçekten çalıştırılıp doğrulanır — tıklanan
    // bir örnek sorunun hata vermesi, sistemin kendi vitrinine güvenmediğini gösterirdi.
    [HttpGet("api/ask/suggestions")]
    public async Task<IActionResult> GetSuggestions(CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync();
        var tenantId = _tenantContext.TenantId ?? Guid.Empty;

        return Ok(await _suggestions.GetAsync(tenantId, catalog, ct));
    }

    // GET /api/ask/conversations — kullanıcının KENDİ sohbetleri, en son konuşulan üstte.
    [HttpGet("api/ask/conversations")]
    public async Task<IActionResult> GetConversations(CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Ok(Array.Empty<ConversationSummary>());

        var conversations = await _db.AskConversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(
                c.Id, c.Title, c.UpdatedAt, c.Messages.Count))
            .ToListAsync(ct);

        return Ok(conversations);
    }

    // GET /api/ask/conversations/{id} — sohbetin turları.
    [HttpGet("api/ask/conversations/{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var conversation = await _db.AskConversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);

        if (conversation is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Sohbet bulunamadı.");

        // Yanıtlar kaydedildiği hâliyle döner; yeniden hesaplanmaz. Veri o günden beri
        // değişmiş olabilir ve geçmiş bir cevabın sonradan değişmesi kafa karıştırırdı.
        // İzlenebilirlik burada, OKUMA ANINDA hesaplanıyor — kaydedilmiş yanıtın içinden
        // okunmuyor. Yanıt donmuş bir kayıttır; "bu cevabı şimdi izlemeye alabilir miyim"
        // ise bugünün sorusu, ve kuralları bugünkü koda göre işlemeli.
        var turns = conversation.Messages
            .Select(m => new ConversationTurn(
                m.Question, JsonDocument.Parse(m.ResponseJson).RootElement, m.CreatedAt,
                m.Id, DescribeUnwatchable(m.PlanJson)))
            .ToList();

        return Ok(new ConversationDetail(conversation.Id, conversation.Title, turns));
    }

    // DELETE /api/ask/conversations/{id}
    [HttpDelete("api/ask/conversations/{id:guid}")]
    public async Task<IActionResult> DeleteConversation(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var conversation = await _db.AskConversations
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);

        if (conversation is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Sohbet bulunamadı.");

        _db.AskConversations.Remove(conversation);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // POST /api/ask — soru sor. Viewer da çağırabilir: sorgu salt okuma bir işlemdir.
    [HttpPost("api/ask")]
    public async Task<IActionResult> Ask(AskRequest request, CancellationToken ct)
    {
        // Arka planda örnek soru üretiliyorsa DURDUR. Ollama istekleri sıraya koyduğundan
        // kullanıcının sorusu o üretimin arkasına düşer ve dakikalarca bekleyebilirdi;
        // süs niteliğindeki bir iş, asıl kullanıcıyı bekletemez.
        _suggestions.NotifyUserActivity(_tenantContext.TenantId ?? Guid.Empty);

        var catalog = await LoadCatalogAsync();

        if (catalog.Datasets.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu firmada henüz veri seti yok.");

        PlanResult planResult;
        try
        {
            planResult = await _planner.PlanAsync(request.Question, catalog, request.Model, ct);
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
            var response = await ExecuteAsync(request.Question, planResult, catalog, ct);

            // Yanıt sohbete kaydedilir; kimliği istemciye dönüyor ki sonraki soru
            // aynı sohbete eklensin. Mesajın kimliği de dönüyor: "bunu izle" ona bakıyor.
            var (conversationId, messageId) =
                await SaveTurnAsync(request.ConversationId, response, planResult, ct);

            return Ok(response with
            {
                ConversationId = conversationId,
                MessageId = messageId,
                WatchBlockReason = WatchEvaluator.DescribeUnwatchable(planResult.Plan)
            });
        }
        catch (InvalidQueryException ex)
        {
            // Model plan üretti ama plan tutarsız. Kullanıcı soruyu değiştirerek çözebilir.
            _logger.LogInformation("Geçersiz plan: {Message}. Soru: {Question}. Plan: {Plan}",
                ex.Message, request.Question, planResult.RawJson);

            return Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }
    }

    private async Task<AskResponse> ExecuteAsync(
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

            return new AskResponse(
                Question: question,
                Kind: kind,
                Summary: PlanSummary.Describe(plan, datasetNames),
                Datasets: Array.Empty<string>(),
                Model: planResult.Model,
                Reason: plan.Reason ?? "Bu soru mevcut verilerle cevaplanamıyor.",
                PlanMs: planResult.DurationMs);
        }

        // Kolon referansları da veriliyor: model gerekli seti join'e eklemeyi unuttuysa
        // ve ilişki tanımlıysa kapsam kendiliğinden genişler (bkz. TenantCatalog).
        // Soruda geçen bir koşul plana hiç yansımamışsa burada dur: filtresiz bir toplam
        // göstermek, hata göstermekten çok daha zararlıdır (kullanıcı yanlış sayıya inanır).
        QueryPlanMapper.ValidateAgainstQuestion(plan, question);

        var scope = catalog.BuildScope(datasetNames, QueryPlanMapper.ColumnReferences(plan));
        var used = scope.Sources.Select(s => s.Name).ToList();
        var summary = PlanSummary.Describe(plan, used);

        var stopwatch = Stopwatch.StartNew();

        if (kind == "rows")
        {
            // plan.Select: yalnız sorulan kolonlar. Verilmezse bütün kolonlar döner.
            // WithIdentityColumn: liste hangi kayıtlara ait belli olsun diye ad/kod kolonu
            // eklenir — soru bunu söylemediğinden modelden beklenmiyor (bkz. QueryPlanMapper).
            var select = QueryPlanMapper.WithIdentityColumn(plan.Select, scope);

            var built = DatasetRowQueryBuilder.BuildSelect(
                QueryPlanMapper.ToRowQuery(plan), scope, select);
            var rows = await _executor.RunRowsAsync(built, ct);
            stopwatch.Stop();

            return new AskResponse(
                question, kind, summary, used,
                Model: planResult.Model,
                Sql: built.Sql,
                PlanMs: planResult.DurationMs,
                QueryMs: (int)stopwatch.ElapsedMilliseconds,
                Rows: new AskRowsResult(built.Columns, rows));
        }

        if (plan.Compare is not null)
            return await CompareAsync(question, planResult, plan, scope, used, summary, stopwatch, ct);

        var aggregateQuery = QueryPlanMapper.ToAggregateQuery(plan);
        var aggregate = DatasetAggregateQueryBuilder.Build(aggregateQuery, scope);
        var buckets = await _executor.RunAggregateAsync(aggregate, ct: ct);
        stopwatch.Stop();

        return new AskResponse(
            question, kind, summary, used,
            Model: planResult.Model,
            Sql: aggregate.Sql,
            PlanMs: planResult.DurationMs,
            QueryMs: (int)stopwatch.ElapsedMilliseconds,
            Aggregate: new AskAggregateResult(
                aggregateQuery.GroupBy, aggregateQuery.Metrics, aggregateQuery.Bucket, buckets));
    }

    // "Bu ay geçen aya göre nasıl?" — tek SQL ile çözülmez: aynı agregasyon iki dönem için
    // çalıştırılıp sonuçlar eşleştirilir. Limit iki sorgudan da çıkarılır, eşleştirmeden
    // SONRA uygulanır (bkz. QueryPlanMapper.ToComparisonQueries).
    private async Task<AskResponse> CompareAsync(
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

        return new AskResponse(
            question, "aggregate", summary, used,
            Model: planResult.Model,
            // İki sorgu çalıştı; kullanıcıya ikisini de gösteriyoruz ki karşılaştırmanın
            // nasıl kurulduğu görünür olsun.
            Sql: $"-- Mevcut dönem\n{currentBuilt.Sql}\n\n-- Önceki dönem\n{previousBuilt.Sql}",
            PlanMs: planResult.DurationMs,
            QueryMs: (int)stopwatch.ElapsedMilliseconds,
            Comparison: new AskComparisonResult(
                plan.Compare!.Period ?? "", plan.Compare.Previous ?? "", merged));
    }

    // Token'daki kullanıcı kimliği. Sohbetler KİŞİSELDİR: aynı firmadaki başka bir
    // kullanıcı bunları göremez, o yüzden tenant filtresi tek başına yetmez.
    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : null;

    // Yanıtı sohbete ekler; sohbet yoksa açar. Sohbetin ve mesajın kimliği döner.
    private async Task<(Guid? ConversationId, Guid? MessageId)> SaveTurnAsync(
        Guid? conversationId, AskResponse response, PlanResult planResult, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return (null, null);

        AskConversation? conversation = null;

        // Kimlik verilmişse KULLANICININ KENDİ sohbeti mi diye bakılır: başkasının
        // sohbetine yazmanın yolu olmamalı.
        if (conversationId is Guid id)
            conversation = await _db.AskConversations
                .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);

        if (conversation is null)
        {
            conversation = new AskConversation
            {
                Id = Guid.NewGuid(),
                UserId = userId.Value,
                Title = MakeTitle(response.Question)
            };
            _db.AskConversations.Add(conversation);
        }

        conversation.UpdatedAt = DateTime.UtcNow;

        var messageId = Guid.NewGuid();

        _db.AskMessages.Add(new AskMessage
        {
            Id = messageId,
            ConversationId = conversation.Id,
            Question = response.Question,
            // Yanıtın tamamı saklanır: geçmiş kayıt yeniden hesaplanmadan, verildiği
            // hâliyle gösterilecek.
            ResponseJson = JsonSerializer.Serialize(response, WebJson),
            // Planın kendisi de saklanır — "bunu izle" dendiğinde çalıştırılacak olan bu.
            PlanJson = planResult.RawJson
        });

        await _db.SaveChangesAsync(ct);
        return (conversation.Id, messageId);
    }

    /// <summary>
    /// Kaydedilmiş bir mesajın izlenememe sebebi; null ise izlenebilir.
    ///
    /// Planı olmayan mesaj izlenemez ve bu gerçek bir durum: izleyicilerden ÖNCE verilmiş
    /// cevapların planı saklanmamıştı. Sessizce izlenebilir göstermek, düğmeye basınca
    /// hata alan bir kullanıcı üretirdi; sebebi burada söyleniyor.
    /// </summary>
    private static string? DescribeUnwatchable(string? planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson))
            return "Bu cevap izlenemez; soruyu yeniden sorup öyle izlemeye alın.";

        var plan = QueryPlanJson.Parse(planJson);
        return plan is null
            ? "Bu cevabın sorgu planı okunamadı."
            : WatchEvaluator.DescribeUnwatchable(plan);
    }

    // Sohbet adı ilk sorudan türetilir; liste okunur kalsın diye kısaltılır.
    private static string MakeTitle(string question)
    {
        var text = question.Trim();
        return text.Length <= 60 ? text : $"{text[..57]}…";
    }

    // İstemciye giden JSON ile AYNI biçim (camelCase): kaydedilen yanıt, canlı yanıttan
    // ayırt edilemez olmalı ki geçmiş sohbet aynı arayüzle çizilebilsin.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // Firmanın kataloğu. Yükleme ortak servise taşındı (bkz. TenantCatalogLoader): aynı
    // kataloğu izleyicinin arka plandaki koşusu da okuyor ve ikisinin ayrışmaması şart.
    private Task<TenantCatalog> LoadCatalogAsync() => _catalogLoader.LoadAsync();
}
