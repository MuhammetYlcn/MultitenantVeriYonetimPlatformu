using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Model seçiminin sunucu tarafındaki denetimi.
//
// Ollama'da kurulu her model plan üretemez: görsel model belgeden veri çıkarmak için
// oradadır. Kurulu olduğu için model listesine düşüyor ve seçilebiliyordu; ondan sorgu
// planı istemek anlamsız bir çıktı üretir. Arayüzden düşürmek yetmez, çünkü uç doğrudan
// çağrılabilir — denetim burada sınanıyor.
public class PlannerModelSelectionTests
{
    private static QueryPlannerService Planner(params string[] nonPlanner)
    {
        var options = Options.Create(new OllamaOptions
        {
            // Adres kullanılmıyor: red kararı Ollama'ya hiç gidilmeden veriliyor.
            BaseUrl = "http://localhost:1",
            Model = "veriyonetim-planlayici:7b-k2",
            NonPlannerModels = nonPlanner
        });

        return new QueryPlannerService(
            new HttpClient { BaseAddress = new Uri("http://localhost:1") },
            options,
            NullLogger<QueryPlannerService>.Instance);
    }

    // Sorgulanabilir bir katalog: red, katalog denetiminden SONRA gelmeli ki testin
    // ölçtüğü şey model seçimi olsun.
    private static TenantCatalog Catalog() => new(
        new[]
        {
            new DatasetInfo(Guid.NewGuid(), "Faturalar", null,
                new Dictionary<string, string> { ["tutar"] = "number" })
        },
        Array.Empty<RelationInfo>());

    [Fact(DisplayName = "Görsel model plan için istenirse reddedilir — Ollama'ya hiç gidilmez")]
    public async Task GorselModelPlanIcinReddedilir()
    {
        var planner = Planner("qwen2.5vl:7b");

        var ex = await Assert.ThrowsAsync<InvalidQueryException>(() =>
            planner.PlanAsync("toplam tutar nedir", Catalog(), model: "qwen2.5vl:7b"));

        // Mesaj kullanıcıya dönük: "kurulu değil" demek yanıltıcı olurdu, model kurulu.
        Assert.Contains("plan", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Ollama adresi kasıtlı olarak ulaşılamaz; servise gidilseydi bağlantı hatası
        // (QueryPlannerException) alırdık. InvalidQueryException gelmesi, kararın
        // ağa çıkmadan verildiğini gösteriyor.
    }

    [Fact(DisplayName = "Varsayılan planlayıcı yanlış ayara rağmen reddedilmez")]
    public async Task VarsayilanPlanlayiciKorunur()
    {
        // Ayar dosyasına yanlışlıkla varsayılan model yazılırsa sistem kendi planlayıcısını
        // reddeder ve kurtarılması zor bir duruma düşerdi.
        var planner = Planner("veriyonetim-planlayici:7b-k2");

        // Red gelmiyor; akış modele ulaşmaya çalışıp bağlantı hatasında düşüyor.
        await Assert.ThrowsAsync<QueryPlannerException>(() =>
            planner.PlanAsync("toplam tutar nedir", Catalog(),
                model: "veriyonetim-planlayici:7b-k2"));
    }
}
