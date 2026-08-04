using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace VeriYonetim.Api.Services;

// appsettings: "Ollama": { "BaseUrl": ..., "Model": ..., "TimeoutSeconds": ... }
public class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5-coder:7b";

    // 7B model bu donanımda ~10 sn'de yanıt veriyor; ilk çağrıda model belleğe yüklendiği
    // için çok daha uzun sürebilir. Sınır ona göre geniş tutuldu.
    public int TimeoutSeconds { get; set; } = 120;
}

// Modelden plan ALINAMADIĞI durumlar: servis kapalı, zaman aşımı, ayrıştırılamayan yanıt.
//
// InvalidQueryException'dan bilinçli olarak AYRI. O, "model plan üretti ama plan geçersiz"
// demektir ve kullanıcı sorusunu değiştirerek çözebilir (400). Bu ise "sistem şu an
// çalışmıyor" demektir; kullanıcının yapabileceği bir şey yoktur (503).
public class QueryPlannerException(string message, Exception? inner = null)
    : Exception(message, inner);

public record PlanResult(QueryPlan Plan, string RawJson, int DurationMs);

public interface IQueryPlannerService
{
    Task<PlanResult> PlanAsync(string question, TenantCatalog catalog,
        CancellationToken ct = default);
}

public class QueryPlannerService : IQueryPlannerService
{
    // Soru uzunluğu sınırı: istem enjeksiyonu için sonsuz alan bırakmamak ve modelin
    // bağlamını şişirmemek için. Doğal bir soru bunun çok altında kalır.
    public const int MaxQuestionLength = 500;

    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly ILogger<QueryPlannerService> _logger;

    public QueryPlannerService(HttpClient http, IOptions<OllamaOptions> options,
        ILogger<QueryPlannerService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PlanResult> PlanAsync(string question, TenantCatalog catalog,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new InvalidQueryException("Soru boş olamaz.");
        if (question.Length > MaxQuestionLength)
            throw new InvalidQueryException($"Soru en fazla {MaxQuestionLength} karakter olabilir.");

        // Şemasız setler modele hiç gösterilmez: model onlardan birini seçerse sorgu
        // kurulamaz ve kullanıcı sebebini anlamayacağı bir hata alırdı.
        if (catalog.Datasets.All(d => d.Columns.Count == 0))
            throw new InvalidQueryException(
                "Sorgulanabilir veri seti yok; önce bir dosya yükleyip şema oluşturun.");

        var prompt = QueryPromptBuilder.Build(question.Trim(), catalog);

        var request = new OllamaGenerateRequest(
            Model: _options.Model,
            Prompt: prompt,
            Stream: false,
            // format=json: Ollama çıktının geçerli JSON olmasını dilbilgisi düzeyinde
            // zorlar. Modelin "İşte planınız:" gibi bir giriş cümlesi yazma ihtimalini
            // ayrıştırma öncesinde ortadan kaldırır.
            Format: "json",
            // temperature=0: aynı soru aynı planı üretsin. Yaratıcılık istemiyoruz;
            // tekrarlanabilirlik hem hata ayıklamayı hem demoyu güvenilir kılar.
            Options: new OllamaModelOptions(Temperature: 0));

        var stopwatch = Stopwatch.StartNew();
        OllamaGenerateResponse? response;

        try
        {
            var http = await _http.PostAsJsonAsync("/api/generate", request, ct);

            if (!http.IsSuccessStatusCode)
                throw new QueryPlannerException(
                    $"Yapay zekâ servisi yanıt vermedi (HTTP {(int)http.StatusCode}). " +
                    $"'{_options.Model}' modeli yüklü mü?");

            response = await http.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // İstek iptali değil, zaman aşımı.
            throw new QueryPlannerException(
                $"Yapay zekâ servisi {_options.TimeoutSeconds} saniyede yanıt veremedi.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new QueryPlannerException(
                $"Yapay zekâ servisine ({_options.BaseUrl}) ulaşılamadı. Ollama çalışıyor mu?", ex);
        }

        stopwatch.Stop();

        var raw = response?.Response;
        if (string.IsNullOrWhiteSpace(raw))
            throw new QueryPlannerException("Yapay zekâ servisi boş yanıt döndürdü.");

        QueryPlan? plan;
        try
        {
            plan = QueryPlanJson.Parse(raw);
        }
        catch (JsonException ex)
        {
            // format=json'a rağmen ayrıştırılamıyorsa yanıtı loglayıp anlaşılır hata veriyoruz.
            _logger.LogWarning(ex, "Model ayrıştırılamayan plan üretti. Yanıt: {Raw}", raw);
            throw new QueryPlannerException("Yapay zekâ geçerli bir sorgu planı üretemedi.", ex);
        }

        if (plan is null)
            throw new QueryPlannerException("Yapay zekâ geçerli bir sorgu planı üretemedi.");

        _logger.LogInformation("Sorgu planı üretildi ({Ms} ms): {Raw}", stopwatch.ElapsedMilliseconds, raw);

        return new PlanResult(plan, raw, (int)stopwatch.ElapsedMilliseconds);
    }

    // --- Ollama HTTP sözleşmesi ---
    // Alan adları Ollama'nın beklediği gibi küçük harf; JsonPropertyName ile sabitlendi ki
    // sunucunun serileştirme ayarları değişse bile istek bozulmasın.

    private record OllamaGenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("options")] OllamaModelOptions Options);

    private record OllamaModelOptions(
        [property: JsonPropertyName("temperature")] double Temperature);

    private record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string? Response);
}
