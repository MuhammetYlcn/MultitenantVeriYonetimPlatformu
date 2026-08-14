using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace VeriYonetim.Api.Services;

// appsettings: "Vision": { "Model": ..., "NumCtx": ..., "MaxLongEdge": ... }
public class VisionOptions
{
    public string Model { get; set; } = "qwen2.5vl:7b";

    // 4096 ÖLÇÜLDÜ, seçilmedi: 8 GB'lık ekran kartında modelin tamamının GPU'da kaldığı
    // en büyük değer bu. 5120 ve 6144 taşıyor, %15'i CPU'ya düşüyor ve belge başına süre
    // ~30 sn'den ~55 sn'ye çıkıyor (1,8 kat).
    public int NumCtx { get; set; } = 4096;

    // Yanıta bırakılan pay. Bağlam istem + görüntü + CEVAP için ortaktır; istem sınıra
    // dayanırsa model cevabını yazacak yer bulamaz.
    public int ReserveTokens { get; set; } = 512;

    // Küçültmenin başlangıç sınırı (uzun kenar, piksel). Ölçümde sabit bir doğru değer
    // bulunamadı — fişte küçültme doğruluğu artırıyor, A4 faturada düşürüyor. Bu yüzden
    // sabit bir boy dayatmak yerine "sığana kadar küçült" politikası uygulanıyor:
    // buradaki değer yalnız üst sınır.
    public int MaxLongEdge { get; set; } = 1800;

    // Bu boyun altına inilmez. Daha da küçültmek belgeyi okunamaz hâle getirir; sığmayan
    // belgeyi sessizce eksik okumaktansa "sığmadı" demek doğrudur.
    public int MinLongEdge { get; set; } = 700;

    // Her denemede uzun kenar bu oranla çarpılır.
    public double ShrinkFactor { get; set; } = 0.75;

    // Toplam deneme sayısı (ilki dahil). Her deneme ~30 sn olduğundan sınırlı tutuldu.
    public int MaxAttempts { get; set; } = 3;

    // Görsel model metin modelinden yavaş: belge başına ~30 sn, ilk çağrıda model belleğe
    // yüklendiği için çok daha uzun.
    public int TimeoutSeconds { get; set; } = 300;
}

// Tek bir model çağrısının ham sonucu.
//
// PromptTokens neden taşınıyor: bu sayı olmadan bağlam taşması GÖRÜNMEZ. Ollama sınırı
// aşan istemi sessizce kırpar ve model, belgenin bir kısmını hiç görmediği hâlde emin bir
// cevap yazar. Sessiz hatayı görünür hataya çeviren tek ölçü bu.
public record VisionCall(string Response, int PromptTokens, int EvalCount);

public interface IOllamaVisionClient
{
    Task<VisionCall> GenerateAsync(string prompt, byte[] jpeg, string model, int numCtx,
        CancellationToken ct = default);
}

// Görsel modelden belge çıkarımının sonucu.
//
// Suspect: çıkarım "güvenilir değil" demek. Kullanıcıya sonuç yine gösterilir (yarısı doğru
// bir okuma da işe yarar) ama işaretli gösterilir — onay ekranı buna bakıp uyarı basar.
public record DocumentExtractionResult(
    ExtractedDocument Document,
    ParsedTable Table,
    string Model,
    int PromptTokens,
    int NumCtx,
    bool Suspect,
    int LongEdge,
    int Attempts,
    int DurationMs,
    IReadOnlyList<string> Warnings);

public interface IDocumentVisionService
{
    Task<DocumentExtractionResult> ExtractAsync(Stream image,
        IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default);

    /// KEŞİF geçişi: hedef şema olmadan okur, kolon adlarını modelin kendisi seçer.
    /// İlk kez görülen belge türü için tek yol — hangi sete yazılacağı, çıkan kolonlara
    /// bakılarak sonra kararlaştırılır (bkz. SchemaMatcher).
    Task<DocumentExtractionResult> DiscoverAsync(Stream image, CancellationToken ct = default);
}

// Belge görüntüsünden yapılandırılmış veri çıkaran katman.
//
// Üç işi var ve üçü de ölçümden çıktı (bkz. PLAN.md "Adım 12"):
//   1. KÜÇÜLTME   — görüntü olduğu gibi gönderilirse bağlamı tek başına doldurabiliyor.
//   2. TAŞMA TESPİTİ — sığmadıysa sonucu şüpheli işaretle; sessiz kırpma en tehlikeli hata.
//   3. AYRIŞTIRMA  — ham metni DocumentExtractionParser'a devret (kurallar orada).
//
// HTTP çağrısı bilinçli olarak ayrı bir arayüzün (IOllamaVisionClient) arkasında: buradaki
// karar mantığı model çalışmadan test edilebilsin diye.
public class DocumentVisionService : IDocumentVisionService
{
    // Qwen2.5-VL görüntüyü 14 piksellik yamalara böler ve 2x2 birleştirir; yani bir görsel
    // belirteç kabaca 28x28 piksele karşılık gelir. Bu bir TAHMİN: gerçek sayı modelin iç
    // yeniden boyutlandırmasına bağlı. Tahmin yalnız İLK denemenin boyunu seçmek için
    // kullanılıyor; taşıp taşmadığına model yanıtındaki gerçek sayıya bakarak karar
    // veriliyor (bkz. Suspect).
    private const int PixelsPerToken = 28 * 28;

    // İstemin metin kısmı için kaba pay (şema listesi + kurallar).
    private const int PromptTextTokens = 400;

    private readonly IOllamaVisionClient _client;
    private readonly VisionOptions _options;
    private readonly ILogger<DocumentVisionService> _logger;

    public DocumentVisionService(IOllamaVisionClient client, IOptions<VisionOptions> options,
        ILogger<DocumentVisionService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public Task<DocumentExtractionResult> ExtractAsync(Stream image,
        IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default)
    {
        if (schema.Count == 0)
            throw new InvalidQueryException(
                "Bu veri setinin şeması tanımlı değil; belge hangi alanlara yazılacağını bilemez.");

        return RunAsync(image, DocumentPromptBuilder.Build(schema), ct);
    }

    public Task<DocumentExtractionResult> DiscoverAsync(Stream image, CancellationToken ct = default)
        => RunAsync(image, DocumentPromptBuilder.BuildDiscovery(), ct);

    // İki geçişin ORTAK yolu: küçültme, taşma tespiti, yeniden deneme, ayrıştırma.
    // Aralarındaki tek fark istem — o yüzden bu mantık iki kez yazılmıyor. (İkiye
    // bölünseydi taşma tespiti gibi bir düzeltme yalnız birine uygulanır, diğeri
    // sessizce eski davranışta kalırdı.)
    private async Task<DocumentExtractionResult> RunAsync(Stream image, string prompt,
        CancellationToken ct)
    {
        using var source = await LoadAsync(image, ct);

        var budget = _options.NumCtx - _options.ReserveTokens - PromptTextTokens;
        var longEdge = Math.Min(_options.MaxLongEdge, FitLongEdge(source.Width, source.Height, budget));

        var warnings = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        VisionCall? call = null;
        var attempt = 0;

        while (attempt < Math.Max(1, _options.MaxAttempts))
        {
            attempt++;
            var jpeg = Encode(source, longEdge);

            call = await _client.GenerateAsync(prompt, jpeg, _options.Model, _options.NumCtx, ct);

            if (!IsOverflow(call.PromptTokens))
                break;

            var smaller = (int)(longEdge * _options.ShrinkFactor);
            if (smaller < _options.MinLongEdge || attempt >= _options.MaxAttempts)
            {
                // Küçültecek yer kalmadı. Sonuç yine döner ama şüpheli işaretlenir:
                // yarısı okunmuş bir belge, hiç okunmamış bir belgeden iyidir — yeter ki
                // kullanıcı hangisine baktığını bilsin.
                warnings.Add($"Belge {_options.NumCtx} belirteçlik bağlama sığmadı " +
                             $"({call.PromptTokens} belirteç). Model belgenin bir kısmını " +
                             "görmemiş olabilir; çıkan değerleri tek tek doğrulayın.");
                break;
            }

            _logger.LogInformation(
                "Belge bağlama sığmadı ({Tokens}/{Ctx}); uzun kenar {Old} → {New} küçültülüyor.",
                call.PromptTokens, _options.NumCtx, longEdge, smaller);

            longEdge = smaller;
        }

        stopwatch.Stop();

        var suspect = call is null || IsOverflow(call.PromptTokens);
        var parsed = call is null ? null : DocumentExtractionParser.Parse(call.Response);

        if (parsed is null)
        {
            // Boş bir belge uydurmuyoruz: okunamadıysa okunamadı denir.
            _logger.LogWarning("Belge çıkarımı ayrıştırılamadı. Ham yanıt: {Raw}", call?.Response);
            throw new InvalidQueryException(
                "Belge okunamadı; görüntü net değilse daha iyi bir çekim deneyin.");
        }

        warnings.AddRange(parsed.Notes);

        return new DocumentExtractionResult(
            parsed,
            DocumentExtractionParser.ToParsedTable(parsed),
            _options.Model,
            call!.PromptTokens,
            _options.NumCtx,
            suspect,
            longEdge,
            attempt,
            (int)stopwatch.ElapsedMilliseconds,
            warnings);
    }

    // İstem bağlamın sonuna dayandıysa Ollama onu sessizce kırpmıştır.
    internal bool IsOverflow(int promptTokens) =>
        promptTokens >= _options.NumCtx - _options.ReserveTokens;

    // Verilen belirteç bütçesine sığacak uzun kenarı hesaplar (en-boy oranı korunur).
    // Saf fonksiyon: doğrudan test edilebilsin diye public.
    public static int FitLongEdge(int width, int height, int tokenBudget)
    {
        if (tokenBudget <= 0) return 1;

        var longEdge = Math.Max(width, height);
        var shortEdge = Math.Min(width, height);
        if (longEdge == 0 || shortEdge == 0) return 1;

        // tokens ≈ (uzun * kısa) / PixelsPerToken  ve  kısa = uzun * oran
        var ratio = (double)shortEdge / longEdge;
        var allowed = Math.Sqrt(tokenBudget * (double)PixelsPerToken / ratio);

        return Math.Max(1, Math.Min(longEdge, (int)allowed));
    }

    private static async Task<Image> LoadAsync(Stream image, CancellationToken ct)
    {
        try
        {
            return await Image.LoadAsync(image, ct);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new InvalidQueryException("Görüntü okunamadı; dosya bozuk olabilir.");
        }
    }

    // Görüntüyü verilen uzun kenara indirip JPEG'e çevirir. Büyütme YAPILMAZ: küçük bir
    // fotoğrafı esnetmek belirteç sayısını artırır, okunabilirliği artırmaz.
    private static byte[] Encode(Image source, int longEdge)
    {
        using var copy = source.Clone(ctx =>
        {
            var current = Math.Max(source.Width, source.Height);
            if (current > longEdge)
                ctx.Resize(new ResizeOptions
                {
                    Size = new Size(longEdge, longEdge),
                    Mode = ResizeMode.Max,          // en-boy oranını korur
                    Sampler = KnownResamplers.Lanczos3
                });
        });

        using var buffer = new MemoryStream();
        copy.SaveAsJpeg(buffer, new JpegEncoder { Quality = 90 });
        return buffer.ToArray();
    }
}

// Ollama'nın görsel uçlarına giden HTTP istemcisi. Metin tarafındaki QueryPlannerService ile
// aynı sözleşme; tek farkı `images` alanı ve `num_ctx` seçeneğinin açıkça verilmesi.
public class OllamaVisionClient : IOllamaVisionClient
{
    private readonly HttpClient _http;

    public OllamaVisionClient(HttpClient http) => _http = http;

    public async Task<VisionCall> GenerateAsync(string prompt, byte[] jpeg, string model,
        int numCtx, CancellationToken ct = default)
    {
        var request = new VisionRequest(
            Model: model,
            Prompt: prompt,
            Images: new[] { Convert.ToBase64String(jpeg) },
            Stream: false,
            // format=json: çıktının geçerli JSON olması dilbilgisi düzeyinde zorlanır.
            Format: "json",
            // temperature=0: aynı belge aynı sonucu versin (ölçüm tekrarlanabilir olmalı).
            Options: new VisionRequestOptions(Temperature: 0, NumCtx: numCtx));

        VisionResponse? response;
        try
        {
            var http = await _http.PostAsJsonAsync("/api/generate", request, ct);

            if (!http.IsSuccessStatusCode)
                throw new QueryPlannerException(
                    $"Görsel model yanıt vermedi (HTTP {(int)http.StatusCode}). " +
                    $"'{model}' modeli yüklü mü?");

            response = await http.Content.ReadFromJsonAsync<VisionResponse>(ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new QueryPlannerException("Görsel model zamanında yanıt veremedi.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new QueryPlannerException(
                "Görsel model servisine ulaşılamadı. Ollama çalışıyor mu?", ex);
        }

        if (string.IsNullOrWhiteSpace(response?.Response))
            throw new QueryPlannerException("Görsel model boş yanıt döndürdü.");

        return new VisionCall(response.Response, response.PromptEvalCount, response.EvalCount);
    }

    private record VisionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("images")] IReadOnlyList<string> Images,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("options")] VisionRequestOptions Options);

    private record VisionRequestOptions(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_ctx")] int NumCtx);

    private record VisionResponse(
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("prompt_eval_count")] int PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int EvalCount);
}
