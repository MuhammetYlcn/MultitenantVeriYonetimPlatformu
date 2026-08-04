using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeriYonetim.Api.Services;

// Dil modelinin ürettiği sorgu planı — SQL DEĞİL, bu.
//
// Model burada yalnızca sabit bir sözlükten seçim yapar (hangi kolon, hangi işlem, hangi
// dönem). Plan sonra QueryPlanMapper'da doğrulanıp builder'a verilir, SQL'i builder yazar.
// Böylece modelin ürettiği hiçbir karakter SQL metnine girmez ve tenant izolasyonu
// modelin bir WHERE koşulunu hatırlamasına bağlı kalmaz.
//
// Tüm alanlar nullable: model eksik/fazla alan üretebilir, deserializasyon buna takılmamalı.
// Asıl doğrulama mapper'da yapılır ve anlaşılır bir hata mesajına dönüşür.
public record QueryPlan(
    // "rows" (satır listesi) | "aggregate" (özet) | "unsupported" (cevaplanamıyor)
    string? Kind = null,

    // Kind = "unsupported" olduğunda kullanıcıya gösterilecek gerekçe.
    string? Reason = null,

    // Sorgunun hangi veri setinden başladığı ve hangilerine bağlandığı. Kullanıcı veri
    // setini seçmez; model kataloğu görüp kendisi karar verir.
    string? From = null,
    IReadOnlyList<string>? Join = null,

    // Her iki mod için ortak. Düz liste = hepsi VE; araya {"logic":"or",...} konursa VEYA.
    IReadOnlyList<PlanFilter>? Filters = null,

    // --- rows ---
    string? Sort = null,
    string? Dir = null,
    int? Limit = null,

    // --- aggregate ---
    IReadOnlyList<string>? GroupBy = null,
    string? Bucket = null,
    IReadOnlyList<PlanMetric>? Metrics = null,
    PlanHaving? Having = null,
    bool Share = false,
    int SortMetric = 0,
    int ShareMetric = 0,
    PlanCompare? Compare = null);

// Filtre düğümü. İKİ şekli var ve ayrımı Logic alanı yapar:
//   yaprak: {"column":"sehir","op":"eq","value":"Ankara"}
//   grup  : {"logic":"or","children":[ ... ]}
//
// Polimorfik JSON (tip ayırt edici alan + özel converter) yerine tek düz kayıt kullanıldı:
// 7B'lik bir modelin üretmesi gereken şema ne kadar sade olursa hata payı o kadar düşük.
public record PlanFilter(
    string? Column = null,
    string? Op = null,
    string? Value = null,
    IReadOnlyList<string>? Values = null,
    string? Logic = null,
    IReadOnlyList<PlanFilter>? Children = null);

public record PlanMetric(string? Op = null, string? Column = null);

public record PlanHaving(int Metric = 0, string? Op = null, decimal Value = 0m);

// Dönem karşılaştırma: aynı agregasyon iki aralık için çalıştırılıp sonuçlar eşleştirilir.
// Column tarih kolonu; Period ve Previous birer RelativePeriod etiketi.
public record PlanCompare(string? Column = null, string? Period = null, string? Previous = null);

// Modelden gelen JSON'u QueryPlan'a çeviren ayarlar.
//
// Dil modeli tip konusunda tutarsızdır: aynı alanı bir seferde "1000", bir seferde 1000
// olarak yazar. Katı deserializasyon bunu çökme sayardı; oysa bu düzeltilebilir bir
// tutarsızlık ve kullanıcıya hata göstermeye değmez.
public static class QueryPlanJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new FlexibleStringConverter() }
    };

    public static QueryPlan? Parse(string json) =>
        JsonSerializer.Deserialize<QueryPlan>(json, Options);

    // string alanlara sayı/bool geldiğinde metne çevirir (modelin en sık tutarsızlığı).
    private sealed class FlexibleStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out var l)
                    ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Metin bekleniyordu, {reader.TokenType} geldi.")
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
