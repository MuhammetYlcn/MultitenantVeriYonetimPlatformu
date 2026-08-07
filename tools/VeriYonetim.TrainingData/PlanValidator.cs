using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using VeriYonetim.Api.Services;

namespace VeriYonetim.TrainingData;

// Üretilen her (soru, plan) çiftini CANLI YOLDAN geçirir.
//
// Neden gerekli? "Doğru cevap" diye modele gösterilen plan gerçekten çalışmıyorsa, model
// çalışmayan bir şeyi taklit etmeyi öğrenir — üstelik bunu fark etmenin tek yolu aylar
// sonra kullanıcının hata alması olur. Burada AskController'ın yaptığı işin aynısı
// yapılıyor, yalnız veritabanına gidilmiyor: builder'lar saf olduğu için SQL metnini
// üretmeleri tek başına bir geçerlilik kanıtıdır (kolon whitelist'i, tip uyumu,
// operatör/işlem denetimi, JOIN kurulabilirliği — hepsi orada işliyor).
public static class PlanValidator
{
    // Türkçe karakterler ç gibi kaçış dizilerine dönmesin: modelin üreteceği metin
    // ham UTF-8, eğitim verisi de öyle olmalı.
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        // JsonNode içinde decimal gibi "özel" değerler yazılırken çözümleyici şart
        // (having.value bir decimal'dir); yoksa yazma anında istisna atılır.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string ToJson(JsonObject plan) => plan.ToJsonString(Compact);

    // Geçerliyse null, değilse anlaşılır hata metni döner.
    public static string? Validate(string question, JsonObject planJson, TenantCatalog catalog)
    {
        QueryPlan? plan;
        try
        {
            plan = QueryPlanJson.Parse(ToJson(planJson));
        }
        catch (JsonException ex)
        {
            return $"JSON ayrıştırılamadı: {ex.Message}";
        }

        if (plan is null) return "Plan boş.";

        try
        {
            var kind = QueryPlanMapper.NormalizeKind(plan);

            if (kind == "unsupported")
                return string.IsNullOrWhiteSpace(plan.Reason)
                    ? "unsupported planında reason yok."
                    : null;

            // İstemdeki kural: rows'ta select zorunlu. Builder bunu dayatmaz (boş select
            // bütün kolonları getirir), ama eğitim verisi kuralı ÇİĞNEMEMELİ — yoksa
            // modele kuralın isteğe bağlı olduğunu öğretmiş oluruz.
            if (kind == "rows" && (plan.Select is null || plan.Select.Count == 0))
                return "rows planında select yok.";

            QueryPlanMapper.ValidateAgainstQuestion(plan, question);

            var scope = catalog.BuildScope(
                QueryPlanMapper.DatasetNames(plan), QueryPlanMapper.ColumnReferences(plan));

            if (kind == "rows")
            {
                DatasetRowQueryBuilder.BuildSelect(
                    QueryPlanMapper.ToRowQuery(plan), scope, plan.Select);
            }
            else if (plan.Compare is not null)
            {
                var (current, previous) = QueryPlanMapper.ToComparisonQueries(plan);
                DatasetAggregateQueryBuilder.Build(current, scope);
                DatasetAggregateQueryBuilder.Build(previous, scope);
            }
            else
            {
                DatasetAggregateQueryBuilder.Build(QueryPlanMapper.ToAggregateQuery(plan), scope);
            }

            return null;
        }
        catch (InvalidQueryException ex)
        {
            return ex.Message;
        }
    }

    // İki planı karşılaştırmak için normal biçim.
    //
    // Ham metin karşılaştırması yanıltıcı olurdu: alan sırası, eksik yazılmış varsayılanlar
    // ve "1000" ile 1000 farkı anlamı değiştirmez. Değerlendirmede ölçmek istediğimiz şey
    // modelin AYNI SORGUYU kurup kurmadığı; nasıl yazdığı değil.
    //
    // Katalog neden gerekli? Kolon referansları nitelikli ("Satislar.adet") ya da sade
    // ("adet") yazılabilir ve ikisi de AYNI kolona çözülür. Metin olarak karşılaştırmak,
    // sade yazan modeli haksız yere yanlış sayardı — üstelik bu, eğitim verisi hep
    // nitelikli yazdığı için yalnız temel modeli cezalandırırdı ve ince ayarın kazancını
    // olduğundan büyük gösterirdi. Referanslar çözümlenip "SetAdı.kolon" biçimine
    // getiriliyor; belirsiz kalan bir ad zaten doğrulamada elenmiş olurdu.
    //
    // includeSelect=false ile İKİNCİ bir ölçüm daha alınıyor: "aynı sorgu ama gösterilecek
    // kolonlar farklı". Buna neden ihtiyaç var? Bazı sorular kolon adı GEÇİRMİYOR:
    //
    //   Soru     : "bugün girilen kayıtlar"
    //   Şablon   : select ["hat","fire"]
    //   Model    : select ["vardiya","uretilen"]
    //
    // İkisi de doğru — soru hangi kolonun gösterileceğini söylemiyor. Tam eşleşmeyi tek
    // ölçüt saymak, cevaplanamayan bir soruda modeli yanlış saymak olur. İki sayı birden
    // raporlanıyor: tam eşleşme (katı) ve select hariç eşleşme (sorgunun kendisi doğru mu).
    public static string Canonical(QueryPlan plan, TenantCatalog catalog, bool includeSelect = true)
    {
        var kind = (plan.Kind ?? "").Trim().ToLowerInvariant();
        var node = new JsonObject { ["kind"] = kind };

        // unsupported'ta gerekçe metni serbesttir; aynı gerekçenin farklı yazımı doğru
        // cevabı yanlış saymamalı. Bu yüzden reason karşılaştırmaya girmiyor.
        if (kind == "unsupported") return node.ToJsonString(Compact);

        QueryScope? scope = null;
        try
        {
            scope = catalog.BuildScope(
                QueryPlanMapper.DatasetNames(plan), QueryPlanMapper.ColumnReferences(plan));
        }
        catch (InvalidQueryException)
        {
            // Çözümlenemeyen plan zaten "geçerli" sayılmadı; ham adlarla devam ediliyor.
        }

        // Kaynak kümesi de çözümlenmiş hâliyle karşılaştırılıyor: model gerekli seti
        // join'e yazmayı atlamış ama kapsam onu kendiliğinden eklemişse (bkz.
        // TenantCatalog.AddSourcesForMissingColumns) ortaya çıkan sorgu aynıdır.
        node["from"] = scope is not null ? scope.Sources[0].Name : (plan.From ?? "").Trim();
        node["sources"] = scope is not null
            ? Sorted(scope.Sources.Select(s => s.Name).ToList())
            : Sorted(plan.Join);

        node["filters"] = CanonFilters(plan.Filters, scope);

        if (kind == "rows")
        {
            if (includeSelect) node["select"] = Listed(plan.Select, scope);
            node["sort"] = Column(plan.Sort, scope);
            node["dir"] = Dir(plan.Dir);
            node["limit"] = plan.Limit ?? QueryPlanMapper.DefaultRowLimit;
            return node.ToJsonString(Compact);
        }

        node["groupBy"] = Listed(plan.GroupBy, scope);
        node["bucket"] = plan.Bucket?.Trim().ToLowerInvariant() ?? "";

        var metrics = new JsonArray();
        foreach (var m in plan.Metrics ?? Array.Empty<PlanMetric>())
            metrics.Add(new JsonObject
            {
                ["op"] = (m.Op ?? "").Trim().ToLowerInvariant(),
                // count kolon istemez; bazı planlar yine de yazar, bazıları yazmaz.
                ["column"] = (m.Op ?? "").Trim().ToLowerInvariant() == "count"
                    ? ""
                    : Column(m.Column, scope)
            });
        node["metrics"] = metrics;

        node["having"] = plan.Having is { } h
            ? new JsonObject
            {
                ["metric"] = h.Metric,
                ["op"] = (h.Op ?? "").Trim(),
                ["value"] = h.Value
            }
            : null;

        node["share"] = plan.Share;
        node["sortMetric"] = plan.SortMetric;
        node["sort"] = plan.Sort?.Trim().ToLowerInvariant() ?? "";
        node["dir"] = Dir(plan.Dir);
        node["limit"] = plan.Limit;

        node["compare"] = plan.Compare is { } cmp
            ? new JsonObject
            {
                ["column"] = Column(cmp.Column, scope),
                ["period"] = cmp.Period?.Trim() ?? "",
                ["previous"] = cmp.Previous?.Trim() ?? ""
            }
            : null;

        return node.ToJsonString(Compact);
    }

    // Kolon referansını "SetAdı.kolon" biçimine getirir. Çözümlenemiyorsa ham metin
    // kalır — karşılaştırma yine yapılabilsin diye.
    private static string Column(string? reference, QueryScope? scope)
    {
        var text = reference?.Trim() ?? "";
        if (scope is null || text.Length == 0) return text;

        try
        {
            var resolved = scope.Resolve(text);
            var source = resolved.Alias is null
                ? scope.Sources[0]
                : scope.Sources.First(s => s.Alias == resolved.Alias);

            return $"{source.Name}.{resolved.Name}";
        }
        catch (InvalidQueryException)
        {
            return text;
        }
    }

    // Sıralaması anlamlı olan listeler (select, groupBy) sırayla korunur.
    private static JsonArray Listed(IReadOnlyList<string>? items, QueryScope? scope)
    {
        var array = new JsonArray();
        foreach (var item in items ?? Array.Empty<string>()) array.Add(Column(item, scope));
        return array;
    }

    private static JsonArray Sorted(IReadOnlyList<string>? items)
    {
        var array = new JsonArray();
        foreach (var item in (items ?? Array.Empty<string>()).Select(i => i.Trim()).OrderBy(i => i))
            array.Add(item);
        return array;
    }

    private static string Dir(string? dir) =>
        string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";

    // Filtreler VE ile bağlandığında sıraları anlamı değiştirmez → sıralanır.
    // Grup düğümlerinin çocukları da aynı mantıkla sıralanır.
    private static JsonArray CanonFilters(IReadOnlyList<PlanFilter>? filters, QueryScope? scope)
    {
        var parts = (filters ?? Array.Empty<PlanFilter>())
            .Select(f => CanonFilter(f, scope))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var array = new JsonArray();
        foreach (var part in parts) array.Add(part);
        return array;
    }

    private static string CanonFilter(PlanFilter f, QueryScope? scope)
    {
        if (!string.IsNullOrWhiteSpace(f.Logic))
        {
            var children = (f.Children ?? Array.Empty<PlanFilter>())
                .Select(child => CanonFilter(child, scope))
                .OrderBy(s => s, StringComparer.Ordinal);

            return $"{f.Logic.Trim().ToLowerInvariant()}({string.Join("|", children)})";
        }

        var values = (f.Values ?? Array.Empty<string>())
            .Select(v => v.Trim())
            .OrderBy(v => v, StringComparer.Ordinal);

        return $"{Column(f.Column, scope)}:{f.Op?.Trim()}:{f.Value?.Trim()}:{string.Join(",", values)}";
    }
}
