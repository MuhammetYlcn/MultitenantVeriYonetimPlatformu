using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

// İzleyiciler — sistemin kendiliğinden konuştuğu yer.
//
// Kullanıcı doğal dilde sorduğu bir soruyu "bunu izle" diyerek kaydeder; sistem onu
// belirli aralıklarla kendi çalıştırır ve sonuç eşiği geçtiğinde haber verir.
//
// İzleyici FİRMAYA aittir, kullanıcıya değil (sohbetlerin aksine): bir uyarı iş
// meselesidir ve kuran kişi izinliyken de görülmelidir. Bu yüzden okuma uçları tenant
// filtresine güvenir, ayrıca kullanıcı filtresi UYGULAMAZ.
//
// Rol kuralları: izleyici KURMAK/DEĞİŞTİRMEK yazma işlemidir (firmanın tamamına uyarı
// göndermeye başlar), o yüzden Editor/Admin ister. Görmek ve okundu işaretlemek herkese
// açık — Viewer zaten aynı soruyu elle sorabiliyor.
[ApiController]
[Route("api/watches")]
[Authorize(Policy = AuthPolicies.TenantUser)]
public class WatchesController : ControllerBase
{
    /// Ayrıntı ekranındaki değer geçmişi grafiği için taşınacak koşu sayısı.
    private const int HistoryLimit = 60;

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IWatchEvaluator _evaluator;
    private readonly IWatchRunner _runner;

    public WatchesController(AppDbContext db, ITenantContext tenantContext,
        IWatchEvaluator evaluator, IWatchRunner runner)
    {
        _db = db;
        _tenantContext = tenantContext;
        _evaluator = evaluator;
        _runner = runner;
    }

    // GET /api/watches — firmanın izleyicileri. Kırık olanlar en üstte: dikkat edilmesi
    // gereken ilk şey çalışmayan alarmdır.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var watches = await _db.DatasetWatches
            .Include(w => w.CreatedByUser)
            .OrderBy(w => w.Status == WatchStatus.Broken ? 0 : w.Status == WatchStatus.Breaching ? 1 : 2)
            .ThenByDescending(w => w.CreatedAt)
            .ToListAsync(ct);

        var unread = await UnreadCountsAsync(ct);

        return Ok(watches.Select(w => ToSummary(w, unread.GetValueOrDefault(w.Id))).ToList());
    }

    // GET /api/watches/{id} — özet + koşu geçmişi (değer geçmişi grafiğinin kaynağı).
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var watch = await _db.DatasetWatches
            .Include(w => w.CreatedByUser)
            .FirstOrDefaultAsync(w => w.Id == id, ct);

        if (watch is null) return NotFound(id);

        var runs = await _db.DatasetWatchRuns
            .Where(r => r.WatchId == id)
            .OrderByDescending(r => r.RanAt)
            .Take(HistoryLimit)
            .ToListAsync(ct);

        var unread = runs.Count(r => r.Notified && r.ReadAt == null);

        // Grafik eskiden yeniye çizilir; sorgu en yeniden aldığı için burada ters çevriliyor.
        runs.Reverse();

        return Ok(new WatchDetail(
            ToSummary(watch, unread),
            watch.Summary,
            runs.Select(r => new WatchRunDto(
                r.Id, r.RanAt, r.Value, r.Breached, r.Error, r.Notified, r.ReadAt)).ToList()));
    }

    // POST /api/watches — bir cevabı izlemeye al.
    [HttpPost]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Create(CreateWatchRequest request, CancellationToken ct)
    {
        if (Validate(request.IntervalMinutes, request.ConditionKind, request.Op) is { } problem)
            return problem;

        var userId = CurrentUserId;

        // Mesaj KULLANICININ KENDİ sohbetinden okunur. Sohbetler kişisel olduğundan
        // başkasının cevabını izlemeye almanın yolu olmamalı.
        var message = await _db.AskMessages
            .FirstOrDefaultAsync(m => m.Id == request.MessageId && m.Conversation.UserId == userId, ct);

        if (message is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Cevap bulunamadı.");

        if (string.IsNullOrWhiteSpace(message.PlanJson))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu cevap izlenemez; soruyu yeniden sorup öyle izlemeye alın.");

        var plan = QueryPlanJson.Parse(message.PlanJson);
        if (plan is null)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu cevabın sorgu planı okunamadı.");

        if (WatchEvaluator.DescribeUnwatchable(plan) is { } reason)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: reason);

        // İzleyici DOĞRULANARAK doğuyor: plan burada bir kez çalıştırılıyor. Çalışmıyorsa
        // izleyici hiç kurulmuyor — kurulup da haftalarca sessiz kalan bir alarmın en kötü
        // hâli, kullanıcının ona güvenmesidir.
        WatchMeasurement measurement;
        try
        {
            measurement = await _evaluator.MeasureAsync(plan, ct);
        }
        catch (InvalidQueryException ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Bu soru şu anda çalıştırılamıyor: {ex.Message}");
        }

        var now = DateTime.UtcNow;

        var watch = new DatasetWatch
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantContext.TenantId ?? Guid.Empty,
            CreatedByUserId = userId,
            Title = MakeTitle(request.Title, message.Question),
            Question = message.Question,
            PlanJson = message.PlanJson,
            Summary = measurement.Summary,
            IntervalMinutes = request.IntervalMinutes,
            ConditionKind = request.ConditionKind,
            ConditionOp = request.Op,
            Threshold = request.Threshold,
            LastValue = measurement.Value,
            LastRunAt = now,
            NextRunAt = now.AddMinutes(request.IntervalMinutes)
        };

        // Kurulduğu anda eşiğin dışındaysa durum baştan "aşıldı" olarak yazılır ve uyarı
        // ÜRETİLMEZ: kullanıcı değeri zaten ekranda görüyor. Bildirim kenar tetiklemeli
        // olduğundan bir sonraki gerçek geçişte gelir.
        watch.IsBreaching = WatchRunner.Evaluate(watch, measurement.Value);
        watch.Status = watch.IsBreaching ? WatchStatus.Breaching : WatchStatus.Ok;

        // İlk ölçüm geçmişe de yazılır: grafiğin ilk noktası izleyicinin doğduğu andır.
        _db.DatasetWatches.Add(watch);
        _db.DatasetWatchRuns.Add(new DatasetWatchRun
        {
            Id = Guid.NewGuid(),
            WatchId = watch.Id,
            RanAt = now,
            Value = measurement.Value,
            Breached = watch.IsBreaching
        });

        await _db.SaveChangesAsync(ct);
        await _db.Entry(watch).Reference(w => w.CreatedByUser).LoadAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = watch.Id }, ToSummary(watch, 0));
    }

    // PATCH /api/watches/{id} — eşik, sıklık, ad ve açık/kapalı.
    //
    // Soru ve planı DEĞİŞTİRİLEMEZ: izlenen ölçüm değişirse değer geçmişi anlamını yitirir.
    // Başka bir şey izlenecekse yeni izleyici kurulur, eskisinin geçmişi bozulmaz.
    [HttpPatch("{id:guid}")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Update(Guid id, UpdateWatchRequest request, CancellationToken ct)
    {
        var watch = await _db.DatasetWatches
            .Include(w => w.CreatedByUser)
            .FirstOrDefaultAsync(w => w.Id == id, ct);

        if (watch is null) return NotFound(id);

        if (Validate(request.IntervalMinutes, request.ConditionKind, request.Op) is { } problem)
            return problem;

        if (!string.IsNullOrWhiteSpace(request.Title)) watch.Title = request.Title.Trim();
        if (request.ConditionKind is { } kind) watch.ConditionKind = kind;
        if (request.Op is { } op) watch.ConditionOp = op;
        if (request.Threshold is { } threshold) watch.Threshold = threshold;

        if (request.IntervalMinutes is { } interval)
        {
            watch.IntervalMinutes = interval;
            // Sıklık değişince sıradaki koşu SON koşuya göre yeniden hesaplanır; aksi hâlde
            // günlükten 15 dakikaya çekilen bir izleyici yine bir gün sonra koşardı.
            watch.NextRunAt = (watch.LastRunAt ?? DateTime.UtcNow).AddMinutes(interval);
        }

        if (request.IsEnabled is { } enabled) watch.IsEnabled = enabled;

        // Eşik değişti: eski "aşıldı" hâli artık yeni eşiğe göre doğru olmayabilir.
        // Yeniden değerlendirilir ki bir sonraki koşu yanlış bir geçiş görmesin —
        // ama uyarı üretilmez, çünkü değişikliği yapan kullanıcı zaten ekranın başında.
        if (watch.Status != WatchStatus.Broken)
        {
            watch.IsBreaching = WatchRunner.Evaluate(watch, watch.LastValue);
            watch.Status = watch.IsBreaching ? WatchStatus.Breaching : WatchStatus.Ok;
        }

        await _db.SaveChangesAsync(ct);

        var unread = await _db.DatasetWatchRuns
            .CountAsync(r => r.WatchId == id && r.Notified && r.ReadAt == null, ct);

        return Ok(ToSummary(watch, unread));
    }

    // POST /api/watches/{id}/run — "şimdi kontrol et".
    //
    // Zamanlanmış koşuyu beklemek yerine elle tetiklemek iki işe yarıyor: kullanıcı
    // izleyicinin gerçekten çalıştığını görebiliyor, ve kırılan bir izleyici düzeltildikten
    // sonra bir sonraki periyodu beklemeden sınanabiliyor.
    [HttpPost("{id:guid}/run")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct)
    {
        var watch = await _db.DatasetWatches
            .Include(w => w.CreatedByUser)
            .FirstOrDefaultAsync(w => w.Id == id, ct);

        if (watch is null) return NotFound(id);

        await _runner.ExecuteAsync(watch, ct);

        var unread = await _db.DatasetWatchRuns
            .CountAsync(r => r.WatchId == id && r.Notified && r.ReadAt == null, ct);

        return Ok(ToSummary(watch, unread));
    }

    // DELETE /api/watches/{id} — koşu geçmişiyle birlikte gider (cascade).
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var watch = await _db.DatasetWatches.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (watch is null) return NotFound(id);

        _db.DatasetWatches.Remove(watch);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // GET /api/watches/alerts — okunmamış uyarılar (bildirim kutusu + rozet sayısı).
    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts(CancellationToken ct)
    {
        var alerts = await _db.DatasetWatchRuns
            .Where(r => r.Notified && r.ReadAt == null)
            .OrderByDescending(r => r.RanAt)
            .Select(r => new WatchAlertDto(
                r.Id, r.WatchId, r.Watch.Title, r.RanAt, r.Value, r.Error, r.Error != null))
            .ToListAsync(ct);

        return Ok(alerts);
    }

    // POST /api/watches/alerts/read — uyarıları okundu işaretle.
    //
    // Rol istemiyor: okundu bilgisi veri değil, görüntüleme durumudur. Firma geneli
    // olduğu için kim gördüyse görülmüş sayılır — uyarı herkese gitti, herkesin ayrı ayrı
    // kapatması gereken bir şey değil.
    [HttpPost("alerts/read")]
    public async Task<IActionResult> MarkRead(MarkAlertsReadRequest request, CancellationToken ct)
    {
        var query = _db.DatasetWatchRuns.Where(r => r.Notified && r.ReadAt == null);

        // Kimlik verilmemişse hepsi okundu sayılır ("tümünü temizle").
        if (request.RunIds is { Count: > 0 } ids)
            query = query.Where(r => ids.Contains(r.Id));

        var runs = await query.ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var run in runs) run.ReadAt = now;

        await _db.SaveChangesAsync(ct);

        return Ok(new { marked = runs.Count });
    }

    // --- yardımcılar ---

    private Guid CurrentUserId =>
        Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : Guid.Empty;

    private ObjectResult NotFound(Guid id) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "İzleyici bulunamadı.");

    /// Sıklık ve koşul doğrulaması. Null geçilen alan "değiştirilmiyor" demektir.
    private ObjectResult? Validate(int? interval, string? kind, string? op)
    {
        if (interval is { } minutes && !WatchIntervals.IsValid(minutes))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Koşu sıklığı şunlardan biri olmalı (dakika): " +
                       $"{string.Join(", ", WatchIntervals.All)}.");

        if (kind is not null && !WatchConditionKind.IsValid(kind))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Koşul türü 'value' ya da 'change' olmalı.");

        if (op is not null && !WatchConditionOps.IsValid(op))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Karşılaştırma şunlardan biri olmalı: {string.Join(", ", WatchConditionOps.All)}.");

        return null;
    }

    private async Task<Dictionary<Guid, int>> UnreadCountsAsync(CancellationToken ct) =>
        await _db.DatasetWatchRuns
            .Where(r => r.Notified && r.ReadAt == null)
            .GroupBy(r => r.WatchId)
            .Select(g => new { WatchId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WatchId, x => x.Count, ct);

    private static WatchSummary ToSummary(DatasetWatch w, int unread) => new(
        w.Id, w.Title, w.Question, w.Status, w.IsEnabled, w.IntervalMinutes,
        w.ConditionKind, w.ConditionOp, w.Threshold,
        w.LastValue, w.PreviousValue, w.LastRunAt, w.LastTriggeredAt, w.NextRunAt,
        w.Error, w.CreatedByUser?.Email ?? "", unread);

    /// Ad verilmemişse sorudan türetilir; liste okunur kalsın diye kısaltılır
    /// (sohbet adıyla aynı kural).
    private static string MakeTitle(string? title, string question)
    {
        var text = (string.IsNullOrWhiteSpace(title) ? question : title).Trim();
        return text.Length <= 60 ? text : $"{text[..57]}…";
    }
}
