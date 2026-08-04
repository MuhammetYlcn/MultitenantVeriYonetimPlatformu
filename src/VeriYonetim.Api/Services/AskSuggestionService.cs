using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeriYonetim.Api.Services;

// Karşılama ekranındaki örnek sorular.
// Ready=false ise henüz üretiliyor demektir; istemci biraz sonra tekrar sorar.
public record SuggestionResult(bool Ready, IReadOnlyList<string> Questions);

public interface IAskSuggestionService
{
    Task<SuggestionResult> GetAsync(Guid tenantId, TenantCatalog catalog, CancellationToken ct = default);

    // Kullanıcı gerçek bir soru sordu: arka plandaki öneri üretimini durdur.
    void NotifyUserActivity(Guid tenantId);
}

// Firmanın kendi verisine göre örnek sorular üretir — ve her birini KULLANICIYA
// GÖSTERMEDEN ÖNCE gerçekten cevaplatır.
//
// Neden doğrulama şart? Modelden soru istemek doğal cümleler verir ama garanti vermez:
// önerdiği soruyu kendisi cevaplayamayabilir ("şu an bunu yapamıyorum") ya da olmayan bir
// kolona dokunabilir. Karşılama ekranında tıklanan örnek soru hata verirse bu, sistemin
// kendi vitrinine güvenmediği anlamına gelir. Bu yüzden her aday arka planda çalıştırılır;
// yalnız gerçekten sonuç dönenler listeye girer.
//
// Maliyet: 1 üretim + N planlama çağrısı, toplam ~30-60 sn. Bu yüzden iş ARKA PLANDA yapılır
// ve sonuç önbelleğe alınır. Önbellek anahtarı kataloğun parmak izidir: veri seti, kolon ya
// da ilişki değişince öneriler kendiliğinden tazelenir.
public class AskSuggestionService : IAskSuggestionService
{
    // Modelden istenen aday sayısı ve listede tutulacak nihai sayı. Adaylar eleneceği
    // için istenen sayı gösterilecek sayıdan fazla.
    private const int CandidateCount = 7;
    private const int KeepCount = 4;

    // Hiç aday doğrulanamadıysa sonuç boş kalır. Bunu sonsuza kadar önbellekte tutmak
    // yanlış olur (veri değişmiş, model daha iyi cevap veriyor olabilir); belirli bir
    // süre sonra yeniden denenir.
    private static readonly TimeSpan EmptyRetryAfter = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AskSuggestionService> _logger;

    // Tenant başına TEK kayıt: yenisi eskisinin üzerine yazılır, kayıt birikmez.
    // Parmak izi de kaydın içinde çünkü "elimdeki hangi kataloğa aitti" bilgisi lazım.
    //
    // Yalnız bellekte: sunucu yeniden başlayınca kaybolur ve öneriler bir süre boş görünür.
    // Bu bilinçli bir tercih — boş karşılama ekranı kabul edilebilir, kalıcılık için bir
    // tablo + migration taşımaya değmez.
    private record CacheEntry(string Fingerprint, IReadOnlyList<string> Questions, DateTime CreatedAt);

    private static readonly ConcurrentDictionary<Guid, CacheEntry> Cache = new();

    // Süren üretimler. Kullanıcı soru sorunca iptal edilirler (bkz. NotifyUserActivity).
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> InProgress = new();

    public AskSuggestionService(IServiceScopeFactory scopes, ILogger<AskSuggestionService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public Task<SuggestionResult> GetAsync(
        Guid tenantId, TenantCatalog catalog, CancellationToken ct = default)
    {
        if (catalog.Datasets.Count == 0)
            return Task.FromResult(new SuggestionResult(true, Array.Empty<string>()));

        var fingerprint = Fingerprint(catalog);

        if (Cache.TryGetValue(tenantId, out var entry) && entry.Fingerprint == fingerprint)
        {
            // Katalog aynı ve elimizde soru var → anında dön.
            if (entry.Questions.Count > 0)
                return Task.FromResult(new SuggestionResult(true, entry.Questions));

            // Boş sonuç: bir süre bekletip yeniden dene.
            if (DateTime.UtcNow - entry.CreatedAt < EmptyRetryAfter)
                return Task.FromResult(new SuggestionResult(true, Array.Empty<string>()));
        }

        // Buraya düşmek şu üç durumdan biri demek: hiç üretilmemiş, katalog değişmiş
        // (yeni veri seti/kolon/ilişki) ya da boş sonucun tazelenme zamanı gelmiş.
        //
        // Eski öneriler bilinçli olarak SERVİS EDİLMİYOR: silinmiş bir kolona dokunan
        // öneri artık çalışmaz, ve bozuk bir öneri boş ekrandan kötüdür.
        var cts = new CancellationTokenSource();
        if (InProgress.TryAdd(tenantId, cts))
            _ = Task.Run(() => GenerateAsync(tenantId, fingerprint, catalog, cts.Token),
                CancellationToken.None);
        else
            cts.Dispose();

        return Task.FromResult(new SuggestionResult(false, Array.Empty<string>()));
    }

    // Kullanıcı soru sorduğunda çağrılır.
    //
    // Neden gerekli? Ollama aynı model için istekleri SIRAYA KOYAR. Arka plan üretimi
    // sekiz çağrı yapıyor; kullanıcı tam o sırada soru sorarsa sorusu bu sekizin arkasına
    // düşer ve dakikalarca bekler. Süs niteliğindeki bir iş, asıl kullanıcıyı bekletemez —
    // bu yüzden üretim iptal edilir ve sonraki fırsatta baştan denenir.
    public void NotifyUserActivity(Guid tenantId)
    {
        if (InProgress.TryGetValue(tenantId, out var running))
            running.Cancel();
    }

    private async Task GenerateAsync(
        Guid tenantId, string fingerprint, TenantCatalog catalog, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var planner = scope.ServiceProvider.GetRequiredService<IQueryPlannerService>();

            var candidates = await AskModelForQuestionsAsync(planner, catalog, ct);
            var verified = new List<string>();

            foreach (var question in candidates)
            {
                if (verified.Count >= KeepCount) break;

                // Kullanıcı araya girdiyse burada bırak: elde ne varsa onunla yetin.
                ct.ThrowIfCancellationRequested();

                // Her aday için TEMİZ bir scope: doğrulama sorguları birbirinin
                // DbContext durumuna karışmasın.
                using var attempt = _scopes.CreateScope();
                if (await CanAnswerAsync(attempt.ServiceProvider, catalog, question, ct))
                    verified.Add(question);
            }

            Cache[tenantId] = new CacheEntry(fingerprint, verified, DateTime.UtcNow);
            _logger.LogInformation("Öneri üretimi bitti: {Kept}/{Total} aday doğrulandı.",
                verified.Count, candidates.Count);
        }
        catch (OperationCanceledException)
        {
            // Kullanıcı araya girdi. Önbelleğe HİÇBİR ŞEY yazmıyoruz ki bir sonraki
            // istekte üretim baştan denensin — yarım kalmış iş sonuç sayılmaz.
            _logger.LogInformation("Öneri üretimi kullanıcı isteği için iptal edildi.");
        }
        catch (Exception ex)
        {
            // Öneriler kozmetik: üretilemezse ekran onlarsız çalışır. Boş sonucu
            // önbelleğe yazıyoruz ki her istekte yeniden denenmesin.
            _logger.LogWarning(ex, "Örnek soru üretimi başarısız.");
            Cache[tenantId] = new CacheEntry(fingerprint, Array.Empty<string>(), DateTime.UtcNow);
        }
        finally
        {
            if (InProgress.TryRemove(tenantId, out var cts)) cts.Dispose();
        }
    }

    // Adayı gerçekten çalıştırır. Cevaplanamıyorsa, plan geçersizse ya da hiç sonuç
    // dönmüyorsa false — boş sonuç dönen bir örnek soru da kötü bir vitrindir.
    private async Task<bool> CanAnswerAsync(
        IServiceProvider services, TenantCatalog catalog, string question, CancellationToken ct)
    {
        try
        {
            var planner = services.GetRequiredService<IQueryPlannerService>();
            var executor = services.GetRequiredService<IDatasetQueryExecutor>();

            var planResult = await planner.PlanAsync(question, catalog, null, ct);
            var plan = planResult.Plan;

            if (QueryPlanMapper.NormalizeKind(plan) == "unsupported") return false;

            // Aday soru bir koşul içeriyor ama plan onu atlamışsa aday elenir — böyle bir
            // öneri tıklandığında sessizce yanlış sayı gösterirdi.
            QueryPlanMapper.ValidateAgainstQuestion(plan, question);

            var scope = catalog.BuildScope(
                QueryPlanMapper.DatasetNames(plan), QueryPlanMapper.ColumnReferences(plan));

            if (plan.Kind?.Trim().ToLowerInvariant() == "rows")
            {
                var built = DatasetRowQueryBuilder.BuildSelect(
                    QueryPlanMapper.ToRowQuery(plan), scope, plan.Select);
                var rows = await executor.RunRowsAsync(built, ct);
                return rows.Count > 0;
            }

            // Karşılaştırma soruları iki sorgu çalıştırır; doğrulamada mevcut dönem yeter.
            var query = plan.Compare is not null
                ? QueryPlanMapper.ToComparisonQueries(plan).Current
                : QueryPlanMapper.ToAggregateQuery(plan);

            var aggregate = DatasetAggregateQueryBuilder.Build(query, scope);
            var buckets = await executor.RunAggregateAsync(aggregate, ct: ct);

            // DİKKAT: "satır döndü mü" yetmez. Gruplamasız bir sorgu hiç kayıt bulamasa
            // bile TEK SATIR döndürür — içindeki toplam NULL, sayaç 0 olur. Yalnızca
            // satır sayısına baksaydık "2023 yılında toplam satış" gibi, veride karşılığı
            // olmayan bir soru geçerli sayılır ve öneri olarak sunulurdu; tıklayan
            // kullanıcı da boş bir cevapla karşılaşırdı.
            return buckets.Any(b => b.Count > 0);
        }
        catch (Exception ex) when (ex is InvalidQueryException or QueryPlannerException)
        {
            _logger.LogDebug("Aday soru elendi ({Reason}): {Question}", ex.Message, question);
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> AskModelForQuestionsAsync(
        IQueryPlannerService planner, TenantCatalog catalog, CancellationToken ct)
    {
        var prompt = BuildQuestionPrompt(catalog);

        // Soru üretimi de plan üretimiyle aynı yoldan geçer: aynı model, aynı JSON kipi.
        // Ayrı bir istemci yazmak yerine planlayıcıya "ham istem" yeteneği eklenmedi;
        // bunun yerine üretim isteği de bir plan gibi JSON döndürüyor.
        var raw = await planner.CompleteJsonAsync(prompt, null, ct);

        try
        {
            var parsed = JsonSerializer.Deserialize<QuestionList>(raw, QueryPlanJson.Options);
            return (parsed?.Questions ?? new List<string>())
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(CandidateCount)
                .ToList();
        }
        catch (JsonException)
        {
            _logger.LogWarning("Aday soru listesi ayrıştırılamadı: {Raw}", raw);
            return Array.Empty<string>();
        }
    }

    private static string BuildQuestionPrompt(TenantCatalog catalog)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Aşağıdaki veri setleri için kullanıcının sorabileceği örnek sorular yaz.");
        sb.AppendLine($"Tam {CandidateCount} soru üret. SADECE JSON döndür: {{\"questions\":[\"...\"]}}");
        sb.AppendLine();

        sb.AppendLine("## Veri setleri");
        foreach (var dataset in catalog.Datasets)
        {
            sb.AppendLine($"- {dataset.Name} ({dataset.RowCount} kayıt)");
            foreach (var column in dataset.Columns)
                sb.AppendLine($"    {column.Key} ({column.Value})");
        }
        sb.AppendLine();

        if (catalog.Relations.Count > 0)
        {
            sb.AppendLine("## İlişkili setler (birleştirilebilir)");
            foreach (var relation in catalog.Relations)
            {
                var from = catalog.Datasets.FirstOrDefault(d => d.Id == relation.FromDatasetId);
                var to = catalog.Datasets.FirstOrDefault(d => d.Id == relation.ToDatasetId);
                if (from is null || to is null) continue;
                sb.AppendLine($"- {from.Name}.{relation.FromColumn} = {to.Name}.{relation.ToColumn}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Kurallar");
        sb.AppendLine("- Sorular Türkçe, kısa ve gündelik dilde olsun (en fazla 8 kelime).");
        sb.AppendLine("- SORU cümlesi yaz, emir kipi KULLANMA.");
        sb.AppendLine("  Doğru: \"Şehirlere göre toplam satış ne kadar?\"");
        sb.AppendLine("  Yanlış: \"Şehirlere göre toplam satışı hesaplayın.\"");
        sb.AppendLine("- SADECE yukarıdaki kolonlarla cevaplanabilecek sorular yaz.");
        sb.AppendLine("- Tahmin, sebep ('neden') ve veride olmayan bilgi sorma.");
        sb.AppendLine("- Sorular birbirinden FARKLI türde olsun: toplama, listeleme, sayma,");
        sb.AppendLine("  zaman içindeki değişim, setleri birleştiren bir soru.");
        sb.AppendLine("- Kimlik/kod kolonlarına göre gruplama sorma (her kaydın değeri farklıdır).");
        sb.AppendLine("- BELİRLİ değer KULLANMA: yıl (2023), şehir adı, kişi adı, ürün adı yazma.");
        sb.AppendLine("  O değerin veride bulunup bulunmadığını bilemezsin; bulunmuyorsa soru boş");
        sb.AppendLine("  ya da yanıltıcı sonuç verir. Genel sor: \"Yıllara göre toplam satış ne kadar?\"");

        return sb.ToString();
    }

    private record QuestionList(
        [property: JsonPropertyName("questions")] List<string>? Questions);

    // Kataloğun parmak izi: veri setleri, kolonları ve ilişkiler. Bunlardan biri
    // değişirse önbellekteki kayıt eskimiş sayılır ve öneriler yeniden üretilir.
    private static string Fingerprint(TenantCatalog catalog)
    {
        var sb = new StringBuilder();

        foreach (var dataset in catalog.Datasets.OrderBy(d => d.Id))
        {
            sb.Append('|').Append(dataset.Id).Append(':');
            foreach (var column in dataset.Columns.OrderBy(c => c.Key))
                sb.Append(column.Key).Append('=').Append(column.Value).Append(',');
        }

        foreach (var relation in catalog.Relations.OrderBy(r => r.FromDatasetId))
            sb.Append('|').Append(relation.FromDatasetId).Append('.').Append(relation.FromColumn)
              .Append('=').Append(relation.ToDatasetId).Append('.').Append(relation.ToColumn);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
