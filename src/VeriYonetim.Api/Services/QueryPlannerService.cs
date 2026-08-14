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

    // Varsayılan planlayıcı: PROJEYE ÖZEL İNCE AYARLI model (bkz. PLAN.md "Adım 11").
    // Ölçümde temel modelin parafraz doğruluğu %87,7 iken bu model %96,5'e çıkıyor ve
    // few-shot örnek gerektirmediği için istemi de kısa tutuyor.
    public string Model { get; set; } = "veriyonetim-planlayici:7b-k2";

    // Plan DIŞINDAKİ serbest metin işleri (ör. örnek soru üretimi) temel modele gider.
    // İnce ayarlı model yalnız plan üretmeye eğitildi: ondan {"questions": [...]} isteyince
    // alışkanlıkla plan biçiminde yanıt verme eğilimi var. Temel model burada daha güvenli.
    public string SuggestionModel { get; set; } = "qwen2.5-coder:7b";

    // Arayüzdeki model seçicide GÖSTERİLMEYECEK modeller.
    //
    // Gizlemek yasaklamak DEĞİL: buradaki modeller API'den hâlâ seçilebilir (karşılaştırma
    // ölçümleri için gerekli) ve kurulu model doğrulaması onları görmeye devam eder.
    // Sadece son kullanıcıya sunulan listeden düşerler.
    public string[] HiddenModels { get; set; } = Array.Empty<string>();

    // 7B model bu donanımda ~10 sn'de yanıt veriyor; ilk çağrıda model belleğe yüklendiği
    // için çok daha uzun sürebilir. Sınır ona göre geniş tutuldu.
    public int TimeoutSeconds { get; set; } = 120;

    // İnce ayarlı (fine-tune edilmiş) modellerin ad öneki.
    //
    // Bu önekle başlayan bir model plan dilini eğitimden bildiği için istemde few-shot
    // örnek GÖRMEZ (bkz. QueryPromptBuilder). Ayrı bir kayıt tutmak yerine ad önekine
    // bakılıyor: modeli Ollama'ya kuran biziz, adını da biz veriyoruz.
    public string FineTunedPrefix { get; set; } = "veriyonetim";
}

// Modelden plan ALINAMADIĞI durumlar: servis kapalı, zaman aşımı, ayrıştırılamayan yanıt.
//
// InvalidQueryException'dan bilinçli olarak AYRI. O, "model plan üretti ama plan geçersiz"
// demektir ve kullanıcı sorusunu değiştirerek çözebilir (400). Bu ise "sistem şu an
// çalışmıyor" demektir; kullanıcının yapabileceği bir şey yoktur (503).
public class QueryPlannerException(string message, Exception? inner = null)
    : Exception(message, inner);

public record PlanResult(QueryPlan Plan, string RawJson, int DurationMs, string Model);

// Kurulu bir model. Boyut ve parametre sayısı arayüzde gösteriliyor: kullanıcı hangi
// modelin daha ağır (ve yavaş) olduğunu görmeden seçim yapamaz.
public record OllamaModel(
    string Name,
    long SizeBytes,
    string? ParameterSize,
    string? Quantization,
    bool IsDefault);

public interface IQueryPlannerService
{
    Task<PlanResult> PlanAsync(string question, TenantCatalog catalog,
        string? model = null, CancellationToken ct = default);

    /// Kurulu modeller. `includeHidden` yalnız DOĞRULAMA yolunda true verilir: gizlenen
    /// bir model arayüzde görünmez ama API'den seçilebilir olmayı sürdürür.
    Task<IReadOnlyList<OllamaModel>> ListModelsAsync(bool includeHidden = false,
        CancellationToken ct = default);

    // Ham istem → ham JSON yanıt. Plan üretimi dışındaki JSON işleri (ör. örnek soru
    // üretimi) ayrı bir Ollama istemcisi yazmak yerine bunu kullanır.
    Task<string> CompleteJsonAsync(string prompt, string? model = null, CancellationToken ct = default);
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

    // Kurulu modeller (Ollama /api/tags). Liste kullanıcıya sunulduğu için ayrıca
    // doğrulama kaynağı: seçilen model bu listede yoksa isteği hiç göndermiyoruz.
    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(bool includeHidden = false,
        CancellationToken ct = default)
    {
        OllamaTagsResponse? tags;
        try
        {
            tags = await _http.GetFromJsonAsync<OllamaTagsResponse>("/api/tags", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new QueryPlannerException(
                $"Yapay zekâ servisine ({_options.BaseUrl}) ulaşılamadı. Ollama çalışıyor mu?", ex);
        }

        return (tags?.Models ?? new List<OllamaTag>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Where(m => includeHidden || !IsHidden(m.Name!))
            .Select(m => new OllamaModel(
                m.Name!,
                m.Size,
                m.Details?.ParameterSize,
                m.Details?.QuantizationLevel,
                IsDefault: m.Name == _options.Model))
            .OrderByDescending(m => m.IsDefault)
            .ThenBy(m => m.Name)
            .ToList();
    }

    // Varsayılan model asla gizlenmez: seçicide hiçbir şey görünmeyen bir ekran, kullanıcıya
    // "yapay zekâ yok" izlenimi verirdi.
    private bool IsHidden(string model) =>
        !string.Equals(model, _options.Model, StringComparison.OrdinalIgnoreCase)
        && _options.HiddenModels.Contains(model, StringComparer.OrdinalIgnoreCase);

    public async Task<PlanResult> PlanAsync(string question, TenantCatalog catalog,
        string? model = null, CancellationToken ct = default)
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

        // Model seçimi kullanıcıdan gelebilir. Serbest metni doğrudan Ollama'ya
        // göndermiyoruz: kurulu modeller arasında yoksa anlaşılır bir hata veriyoruz
        // (aksi halde kullanıcı Ollama'nın ham "model not found" hatasını görürdü).
        var selected = _options.Model;
        if (!string.IsNullOrWhiteSpace(model) && model.Trim() != _options.Model)
        {
            // Gizlenenler dahil: arayüzde görünmeyen bir model API'den seçilebilir olmalı
            // (karşılaştırma ölçümleri temel modeli böyle çağırıyor).
            var installed = await ListModelsAsync(includeHidden: true, ct);
            var match = installed.FirstOrDefault(m =>
                string.Equals(m.Name, model.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is null)
                throw new InvalidQueryException(
                    $"'{model}' modeli kurulu değil. Kurulu modeller: " +
                    string.Join(", ", installed.Select(m => m.Name)));

            selected = match.Name;
        }

        // Örnekler yalnız temel modele gönderilir; ince ayarlı model deseni ağırlıklarından
        // bilir ve aynı örnekleri tekrar okumak istemi iki katına çıkarıp yanıtı yavaşlatır.
        var prompt = QueryPromptBuilder.Build(question.Trim(), catalog, !IsFineTuned(selected));

        var stopwatch = Stopwatch.StartNew();
        var raw = await GenerateAsync(prompt, selected, ct);
        stopwatch.Stop();

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

        _logger.LogInformation("Sorgu planı üretildi ({Model}, {Ms} ms): {Raw}",
            selected, stopwatch.ElapsedMilliseconds, raw);

        return new PlanResult(plan, raw, (int)stopwatch.ElapsedMilliseconds, selected);
    }

    // Eğitim verisi üreteci de bunu kullanır: eğitimdeki istem ile canlıdaki istem birebir
    // aynı olmalı, yoksa model tanımadığı bir biçim görür ve doğruluk düşer.
    public bool IsFineTuned(string model) =>
        !string.IsNullOrWhiteSpace(_options.FineTunedPrefix) &&
        model.StartsWith(_options.FineTunedPrefix, StringComparison.OrdinalIgnoreCase);

    // Model verilmezse SuggestionModel kullanılır, varsayılan planlayıcı değil: buradan
    // geçen işler plan üretimi değil (bkz. OllamaOptions.SuggestionModel).
    public Task<string> CompleteJsonAsync(string prompt, string? model = null,
        CancellationToken ct = default) =>
        GenerateAsync(prompt, string.IsNullOrWhiteSpace(model) ? FreeTextModel : model.Trim(), ct);

    private string FreeTextModel => string.IsNullOrWhiteSpace(_options.SuggestionModel)
        ? _options.Model
        : _options.SuggestionModel;

    // Ollama'ya tek bir üretim isteği atar ve ham yanıt metnini döndürür.
    private async Task<string> GenerateAsync(string prompt, string model, CancellationToken ct)
    {
        var request = new OllamaGenerateRequest(
            Model: model,
            Prompt: prompt,
            Stream: false,
            // format=json: Ollama çıktının geçerli JSON olmasını dilbilgisi düzeyinde
            // zorlar. Modelin "İşte planınız:" gibi bir giriş cümlesi yazma ihtimalini
            // ayrıştırma öncesinde ortadan kaldırır.
            Format: "json",
            // temperature=0: aynı soru aynı planı üretsin. Yaratıcılık istemiyoruz;
            // tekrarlanabilirlik hem hata ayıklamayı hem demoyu güvenilir kılar.
            Options: new OllamaModelOptions(Temperature: 0));

        OllamaGenerateResponse? response;
        try
        {
            var http = await _http.PostAsJsonAsync("/api/generate", request, ct);

            if (!http.IsSuccessStatusCode)
                throw new QueryPlannerException(
                    $"Yapay zekâ servisi yanıt vermedi (HTTP {(int)http.StatusCode}). " +
                    $"'{model}' modeli yüklü mü?");

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

        var raw = response?.Response;
        if (string.IsNullOrWhiteSpace(raw))
            throw new QueryPlannerException("Yapay zekâ servisi boş yanıt döndürdü.");

        return raw;
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

    private record OllamaTagsResponse(
        [property: JsonPropertyName("models")] List<OllamaTag>? Models);

    private record OllamaTag(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("details")] OllamaTagDetails? Details);

    private record OllamaTagDetails(
        [property: JsonPropertyName("parameter_size")] string? ParameterSize,
        [property: JsonPropertyName("quantization_level")] string? QuantizationLevel);
}
