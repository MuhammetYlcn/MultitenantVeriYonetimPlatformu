using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeriYonetim.TrainingData;

public static class Json
{
    // Türkçe karakterler kaçış dizisine dönmesin: dosyalar hem modelin göreceği hâliyle
    // aynı olsun hem de gözle okunabilsin (üretilen veriyi denetlemenin tek yolu okumak).
    public static readonly JsonSerializerOptions Jsonl = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

// Ara biçim: soru + doğru plan. İstem burada YOK — istem biçimi değişince bütün veriyi
// yeniden üretmek gerekmesin diye, istem yalnız "build" adımında ekleniyor.
// Origin: parafrazın hangi şablon sorusundan türediği (izlenebilirlik).
public record SampleRecord(
    string Question,
    string Plan,
    string Recipe,
    string Catalog,
    string Split = "",
    string Source = "sablon",
    string? Origin = null);

// Eğitime giren nihai satır: modelin göreceği istem + üretmesi beklenen plan.
public record TrainingRow(
    string Prompt,
    string Completion,
    string Question,
    string Recipe,
    string Catalog,
    string Split,
    string Source);

// Diske yazılan ölçüm satırı. Ham model yanıtı ("uretilen") saklanıyor: puanlama ölçütü
// değiştiğinde modeli yeniden çalıştırmadan yeniden puanlanabilsin diye (bkz. rescore).
public record SavedResult(
    string Soru,
    string Bolum,
    string Tarif,
    string Beklenen,
    string Uretilen,
    bool Gecerli,
    bool Dogru,
    string Not);

public record EvalResult(
    TrainingRow Row,
    string Raw,
    bool Parsed,
    bool Valid,
    bool Exact,
    string Note);

// Ollama'ya konuşan ince istemci.
//
// API projesindeki QueryPlannerService kullanılmıyor: o sınıf DI, ILogger ve IOptions
// istiyor; burada tek ihtiyaç duyulan şey ham bir üretim çağrısı. Sözleşme (model/prompt/
// format/options) aynı tutuldu ki ölçülen davranış canlıdakiyle örtüşsün.
public sealed class Ollama
{
    private readonly HttpClient _http;
    private readonly string _model;

    public Ollama(string model, string baseUrl = "http://localhost:11434")
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            // Toplu iş: tek tek isteklerin zaman aşımına düşmesindense beklemek yeğdir.
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    // Plan üretimi: canlıdaki ayarların aynısı (format=json, temperature=0).
    public Task<string> GenerateAsync(string prompt) => PostAsync(prompt, temperature: 0);

    // Soruyu yeniden yazdırır.
    //
    // context: sorunun dokunduğu veri seti ve kolonlar. Şema verilmeden yazdırıldığında
    // model bağlamı UYDURUYOR — "miktar" sorusunu "harcamalarınız"a çeviriyor, oysa
    // öyle bir kolon yok. Şemayı görünce alan adlarına sadık kalıyor.
    //
    // Sıcaklık orta: 0.9'da cümleler dağılıp anlam kayıyordu, 0'da hepsi aynı çıkıyor.
    public async Task<string[]> ParaphraseAsync(string question, string context, int count)
    {
        // $$ ile: tek küme ayracı düz metin (JSON örneği), çift küme ayracı ise değer yeri.
        var prompt = $$"""
            Bir veri sorgulama uygulamasına sorulmuş şu soruyu {{count}} farklı şekilde yeniden yaz.
            Amaç: aynı sorunun günlük iş Türkçesinde başka türlü nasıl sorulacağını göstermek.

            Sorunun dayandığı veriler:
            {{context}}

            KURALLAR
            - Anlam BİREBİR aynı kalacak. Aynı kolonlar, aynı koşullar, aynı hesap.
            - Yukarıdaki kolon adlarını kullan. Listede olmayan bir kavram UYDURMA
              (bütçe, harcama, performans gibi kelimeler ekleme).
            - Özel adlar, sayılar, kodlar ve tarihler AYNEN kalacak.
            - Olumsuzluk ekleme veya çıkarma ("...sız", "...değil", "hariç").
            - TEK cümle yaz. Uygulamaya yazılan bir istek gibi olsun.
            - Kendinden söz etme ("başlatıyorum", "ister misiniz" gibi ifadeler yok).
            - Sadece JSON döndür: {"varyantlar": ["...", "..."]}

            Soru: {{question}}
            """;

        var raw = await PostAsync(prompt, temperature: 0.6);

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("varyantlar", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return list.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private async Task<string> PostAsync(string prompt, double temperature)
    {
        var response = await _http.PostAsJsonAsync("/api/generate", new
        {
            model = _model,
            prompt,
            stream = false,
            format = "json",
            options = new { temperature }
        });

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Ollama HTTP {(int)response.StatusCode} — '{_model}' modeli yüklü mü?");

        var payload = await response.Content.ReadFromJsonAsync<GenerateResponse>();
        return payload?.Response ?? throw new InvalidOperationException("Ollama boş yanıt döndürdü.");
    }

    private record GenerateResponse([property: JsonPropertyName("response")] string? Response);
}
