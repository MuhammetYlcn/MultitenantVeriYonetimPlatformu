using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VeriYonetim.Api.Services;
using VeriYonetim.TrainingData;
using static VeriYonetim.TrainingData.Json;

// Doğal dil → sorgu planı modelinin eğitim verisini üreten, zenginleştiren ve ölçen araç.
//
//   generate    şablonlardan (soru, plan) çiftleri üretir ve doğrular
//   paraphrase  soruları yerel modele yeniden yazdırıp dil çeşitliliğini artırır
//   build       örnekleri eğitim biçimine (istem + beklenen çıktı) çevirir
//   evaluate    bir modelin plan doğruluğunu ölçer
//
// Örnek:
//   dotnet run --project tools/VeriYonetim.TrainingData -- generate --out data
//   dotnet run --project tools/VeriYonetim.TrainingData -- build --out data
//   dotnet run --project tools/VeriYonetim.TrainingData -- evaluate --in data/eval.jsonl

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var options = ParseArgs(args.Skip(1));

return command switch
{
    "generate" => await Generate(options),
    "paraphrase" => await Paraphrase(options),
    "build" => Build(options),
    "evaluate" => await Evaluate(options),
    "rescore" => Rescore(options),
    _ => Help()
};

static int Help()
{
    Console.WriteLine("""
        Kullanım: dotnet run -- <komut> [seçenekler]

          generate    --out <dizin> [--train 4000] [--eval 150] [--seed 42]
          paraphrase  --out <dizin> [--model qwen2.5-coder:7b] [--variants 2]
          build       --out <dizin>
          evaluate    --in <dosya> [--model qwen2.5-coder:7b] [--limit 0]
          rescore     --in <sonuc dosyasi> [--samples data/samples.eval.jsonl]
        """);
    return 1;
}

// ============================ generate ============================

static async Task<int> Generate(Dictionary<string, string> options)
{
    await Task.CompletedTask;

    var outDir = options.GetValueOrDefault("out", "data");
    var trainCount = int.Parse(options.GetValueOrDefault("train", "4000"));
    var evalCount = int.Parse(options.GetValueOrDefault("eval", "150"));
    var seed = int.Parse(options.GetValueOrDefault("seed", "42"));

    Directory.CreateDirectory(outDir);

    var rejects = new Dictionary<string, int>();

    // Eğitim kataloglarından TEK bir havuz üretilip ikiye bölünüyor. Ayrı ayrı üretilseydi
    // aynı şablon aynı soruyu hem eğitime hem değerlendirmeye koyabilir, doğruluk sayısı
    // ezberi ölçerdi.
    var pool = Produce(CatalogDefs.Training.ToList(), trainCount + evalCount, seed, rejects);
    Shuffle(pool, new Random(seed + 1));

    var evalSeen = pool.Take(evalCount).Select(s => s with { Split = "eval-seen" }).ToList();
    var train = pool.Skip(evalCount).Select(s => s with { Split = "train" }).ToList();

    // Hiç görülmemiş şemalar: asıl ölçüm bunlar. Yeni bir firma sisteme girdiğinde
    // kolon adları farklı olacak; modelin ezberi değil genellemesi işe yarayacak.
    var evalUnseen = Produce(CatalogDefs.Evaluation.ToList(), evalCount, seed + 2, rejects)
        .Select(s => s with { Split = "eval-unseen" })
        .ToList();

    WriteJsonl(Path.Combine(outDir, "samples.train.jsonl"), train);
    WriteJsonl(Path.Combine(outDir, "samples.eval.jsonl"), evalSeen.Concat(evalUnseen));

    Console.WriteLine($"Eğitim örneği     : {train.Count}");
    Console.WriteLine($"Değerlendirme     : {evalSeen.Count} görülmüş şema + {evalUnseen.Count} görülmemiş şema");
    Console.WriteLine($"Tarif sayısı      : {Recipes.All.Count}");
    Console.WriteLine();
    Console.WriteLine("Tarif dağılımı (eğitim):");
    foreach (var group in train.GroupBy(s => s.Recipe).OrderByDescending(x => x.Count()))
        Console.WriteLine($"  {group.Key,-24} {group.Count()}");

    if (rejects.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Doğrulamada elenenler (tarif → sebep sayısı):");
        foreach (var entry in rejects.OrderByDescending(e => e.Value))
            Console.WriteLine($"  {entry.Value,5}  {entry.Key}");
    }

    return 0;
}

// Şablonlardan benzersiz örnek havuzu üretir. Her örnek üretildiği anda doğrulanır;
// geçmeyen atılır ve sebebi sayılır (şablon hatalarını görünür kılmak için).
static List<SampleRecord> Produce(
    IReadOnlyList<CatalogDef> catalogs, int target, int seed, Dictionary<string, int> rejects)
{
    var gen = new Gen(new Random(seed));
    var rnd = new Random(seed);
    var catalogCache = catalogs.ToDictionary(c => c.Name, c => c.ToCatalog());

    var samples = new List<SampleRecord>(target);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Üst sınır: şablon çeşitliliği tükendiğinde sonsuz döngüye girmemek için.
    var attempts = 0;
    var maxAttempts = target * 60;

    while (samples.Count < target && attempts < maxAttempts)
    {
        // Tarifler SIRAYLA dolaşılıyor, rastgele seçilmiyor. Rastgele seçimde çok
        // varyasyon üretebilen tarifler (zaman serisi) veriyi doldurup az varyasyonlu
        // olanları (JOIN'li satır sorgusu) bir avuç örnekle bırakıyordu; model de az
        // gördüğü deseni öğrenmiyor. Sıra usulü her tarife eşit hak tanır, tarif kendi
        // benzersiz soru havuzunu tükettiğinde kendiliğinden geri çekilir.
        var recipe = Recipes.All[attempts % Recipes.All.Count];
        var catalog = catalogs[rnd.Next(catalogs.Count)];

        attempts++;

        var draft = recipe.Make(gen, catalog);
        if (draft is null) continue;

        var question = draft.Question.Trim();
        if (!seen.Add(Normalize(question))) continue;

        var error = PlanValidator.Validate(question, draft.Plan, catalogCache[catalog.Name]);
        if (error is not null)
        {
            var key = $"{recipe.Name}: {error}";
            rejects[key] = rejects.GetValueOrDefault(key) + 1;
            continue;
        }

        samples.Add(new SampleRecord(
            question,
            PlanValidator.ToJson(draft.Plan),
            recipe.Name,
            catalog.Name,
            Split: "",
            Source: "sablon"));
    }

    return samples;
}

// ============================ paraphrase ============================

static async Task<int> Paraphrase(Dictionary<string, string> options)
{
    var outDir = options.GetValueOrDefault("out", "data");
    var model = options.GetValueOrDefault("model", "qwen2.5-coder:7b");
    var variants = int.Parse(options.GetValueOrDefault("variants", "2"));

    var inputPath = Path.Combine(outDir, "samples.train.jsonl");
    var outputPath = Path.Combine(outDir, "samples.train.para.jsonl");

    var samples = ReadJsonl(inputPath).ToList();
    var catalogCache = CatalogDefs.All.ToDictionary(c => c.Name, c => c.ToCatalog());

    // Sürdürülebilirlik: saatler süren bir iş yarıda kesilebilir. Çıktıda zaten işlenmiş
    // sorular varsa baştan başlamıyoruz.
    var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (File.Exists(outputPath))
        foreach (var existing in ReadJsonl(outputPath))
        {
            done.Add(Normalize(existing.Question));
            if (existing.Origin is not null) done.Add(Normalize(existing.Origin));
        }

    var ollama = new Ollama(model);
    await using var writer = new StreamWriter(outputPath, append: true, new UTF8Encoding(false));

    int accepted = 0, rejected = 0, processed = 0;
    var started = DateTime.Now;

    foreach (var sample in samples)
    {
        if (!done.Add(Normalize(sample.Question)))
            continue;

        processed++;

        // Şablon sorusu her hâlükârda korunuyor: parafraz bir EK, bir yerine geçme değil.
        writer.WriteLine(JsonSerializer.Serialize(sample, Jsonl));

        var required = RequiredLabels(sample);
        var features = RequiredFeatures(sample);

        string[] rewrites;
        try
        {
            rewrites = await ollama.ParaphraseAsync(sample.Question, CatalogContext(sample), variants);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[hata] {ex.Message}");
            continue;
        }

        foreach (var rewrite in rewrites)
        {
            var candidate = rewrite.Trim();

            if (!IsFaithful(sample.Question, candidate, required, features) || !done.Add(Normalize(candidate)))
            {
                rejected++;
                continue;
            }

            // Plan aynı kaldığı için parafrazın da AYNI planla geçerli olması şart.
            // En sık yakalanan hata: modelin cümleye yıl eklemesi ("2024'te ne kadar?"),
            // ki o zaman plandaki tarih koşuluyla soru birbirini tutmaz.
            var planNode = JsonNode.Parse(sample.Plan)!.AsObject();
            if (PlanValidator.Validate(candidate, planNode, catalogCache[sample.Catalog]) is not null)
            {
                rejected++;
                continue;
            }

            writer.WriteLine(JsonSerializer.Serialize(
                sample with { Question = candidate, Source = "parafraz", Origin = sample.Question },
                Jsonl));
            accepted++;
        }

        if (processed % 25 == 0)
        {
            await writer.FlushAsync();
            var elapsed = DateTime.Now - started;
            var rate = processed / Math.Max(elapsed.TotalSeconds, 1);
            var remaining = TimeSpan.FromSeconds((samples.Count - processed) / Math.Max(rate, 0.0001));
            Console.WriteLine(
                $"{processed}/{samples.Count} — kabul {accepted}, ret {rejected}, " +
                $"kalan ~{remaining:hh\\:mm}");
        }
    }

    Console.WriteLine($"Bitti. Kabul edilen parafraz: {accepted}, elenen: {rejected}");
    return 0;
}

// Parafraz sadakat denetimi.
//
// Model yeniden yazarken sessizce bilgi ekleyip çıkarabilir — "Ankara'daki satışlar"
// sorusunu "büyük şehirlerdeki satışlar"a çevirirse plan artık o soruya ait değildir.
// Bozuk bir parafraz, parafraz yokluğundan DAHA kötüdür: modele yanlış eşleme öğretir.
//
// requiredLabels: planın hesapladığı/grupladığı/filtrelediği kolonların adları. Bunlar
// soruda mutlaka geçmeli — ilk denemede en çok zararı "miktar"ı "harcamalarınız"a
// çevirmek gibi kavram uydurmaları verdi.
static bool IsFaithful(string original, string candidate,
    IReadOnlyList<string> requiredLabels, IReadOnlyList<string[]> requiredFeatures)
{
    if (candidate.Length < 5 || candidate.Length > 200) return false;
    if (string.Equals(Normalize(original), Normalize(candidate), StringComparison.Ordinal)) return false;

    // Tek cümle olmalı: model bazen soruyu açıklamayla birlikte iki cümleye yayıyor.
    if (candidate.Count(ch => ch is '?' or '.' or '!') > 1) return false;

    foreach (var token in SignificantTokens(original))
        if (!candidate.Contains(token, StringComparison.OrdinalIgnoreCase))
            return false;

    // Ters yön: soruda olmayan bir sayı eklenmiş mi (uydurulmuş eşik/yıl).
    foreach (var token in SignificantTokens(candidate))
        if (token.Any(char.IsDigit) && !original.Contains(token, StringComparison.OrdinalIgnoreCase))
            return false;

    // Kolon adları korunmuş mu. Ek almış hâlleri de geçsin diye kökle karşılaştırılıyor
    // ("miktar" → "miktarı", "miktarın").
    foreach (var stem in requiredLabels)
        if (!candidate.Contains(stem, StringComparison.OrdinalIgnoreCase))
            return false;

    // Planın ayırt edici yeteneği cümlede hâlâ duruyor mu. En sık kaybolan buydu:
    // "adet dağılımının YÜZDESİ" sorusu "adet dağılımı"na inince plandaki share:true
    // sorunun karşılığı olmaktan çıkıyor — ve bu, kaybı en pahalı olan yeteneklerde
    // (medyan, pay, dönem karşılaştırması) oluyor.
    foreach (var alternatives in requiredFeatures)
        if (!alternatives.Any(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase)))
            return false;

    // Sohbete ya da yönergeye kayan kalıplar: bunlar artık bir veri sorusu değil.
    foreach (var marker in DriftMarkers())
        if (candidate.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return false;

    return true;
}

// Planın nadir yeteneklerini cümlede tutan anahtar kelimeler. Her küme için EN AZ BİRİ
// bulunmalı; eş anlamlıya izin var, kavramın tümden düşmesine yok.
static IReadOnlyList<string[]> RequiredFeatures(SampleRecord sample)
{
    QueryPlan? plan;
    try { plan = QueryPlanJson.Parse(sample.Plan); }
    catch (JsonException) { return Array.Empty<string[]>(); }
    if (plan is null) return Array.Empty<string[]>();

    var groups = new List<string[]>();

    if (plan.Share)
        groups.Add(new[] { "yüzde", "pay", "oran" });

    if (plan.Compare is not null)
        groups.Add(new[] { "geçen", "önceki", "kıyas", "karşılaştır", "göre" });

    foreach (var op in (plan.Metrics ?? Array.Empty<PlanMetric>())
                 .Select(m => (m.Op ?? "").Trim().ToLowerInvariant()))
    {
        if (op == "median") groups.Add(new[] { "medyan", "ortanca", "tipik" });
        if (op == "countdistinct") groups.Add(new[] { "farklı", "değişik", "benzersiz", "tekrarsız", "kaç tür" });
        if (op == "avg") groups.Add(new[] { "ortalama", "ortalamas" });
    }

    // Zaman kovası: soru hangi çözünürlükte istendiğini söylemeli, yoksa plan keyfî kalır.
    var bucket = (plan.Bucket ?? "").Trim().ToLowerInvariant();
    if (bucket == "day") groups.Add(new[] { "gün" });
    if (bucket == "week") groups.Add(new[] { "hafta" });
    if (bucket == "month") groups.Add(new[] { "ay" });
    if (bucket == "year") groups.Add(new[] { "yıl" });

    // Cevaplanamayan sorularda kavram tümden kaymasın.
    if (string.Equals(plan.Kind?.Trim(), "unsupported", StringComparison.OrdinalIgnoreCase))
        groups.Clear();

    return groups;
}

static string[] DriftMarkers() => new[]
{
    "ister misin", "başlatıyorum", "veriyorum", "yapabilir miyim", "nasıl yapabilir",
    "yardımcı ol", "size ", "sizin ", "senin ", "benim için"
};

// Planın gerçekten adını andığı kolonların etiket kökleri.
//
// Tarih kolonları DIŞARIDA: "bu ay yapılan satışlar" sorusunda plan tarih kolonuna
// filtre koyar ama soru "tarih" kelimesini hiç geçirmez — zorunlu tutmak bütün geçerli
// parafrazları elerdi. select'teki kolonlar da aynı sebeple dışarıda.
static IReadOnlyList<string> RequiredLabels(SampleRecord sample)
{
    var catalog = CatalogDefs.All.FirstOrDefault(c => c.Name == sample.Catalog);
    if (catalog is null) return Array.Empty<string>();

    QueryPlan? plan;
    try { plan = QueryPlanJson.Parse(sample.Plan); }
    catch (JsonException) { return Array.Empty<string>(); }
    if (plan is null) return Array.Empty<string>();

    var names = new List<string>();
    foreach (var g in plan.GroupBy ?? Array.Empty<string>()) names.Add(g);
    foreach (var m in plan.Metrics ?? Array.Empty<PlanMetric>())
        if (m.Column is not null) names.Add(m.Column);
    CollectFilterColumns(plan.Filters, names);

    var stems = new List<string>();

    foreach (var reference in names)
    {
        var dot = reference.LastIndexOf('.');
        var bare = dot >= 0 ? reference[(dot + 1)..] : reference;

        var column = catalog.Datasets
            .SelectMany(d => d.Columns)
            .FirstOrDefault(c => c.Name == bare);

        if (column is null || column.Type == "date") continue;

        // Etiketin en uzun kelimesinin kökü. "ad" gibi çok kısa kelimeler her metne
        // uyar, o yüzden atlanıyor: yanlış bir kabul, gereksiz bir retten kötüdür.
        var word = column.Label
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .OrderByDescending(w => w.Length)
            .FirstOrDefault() ?? "";

        if (word.Length < 3) continue;

        stems.Add(word[..Math.Min(5, word.Length)]);
    }

    return stems.Distinct().ToList();
}

static void CollectFilterColumns(IReadOnlyList<PlanFilter>? filters, List<string> into)
{
    foreach (var f in filters ?? Array.Empty<PlanFilter>())
    {
        if (!string.IsNullOrWhiteSpace(f.Logic)) CollectFilterColumns(f.Children, into);
        else if (!string.IsNullOrWhiteSpace(f.Column)) into.Add(f.Column);
    }
}

// Parafraz istemine konan şema özeti. Model kolon adlarını görmezse uyduruyor.
static string CatalogContext(SampleRecord sample)
{
    var catalog = CatalogDefs.All.FirstOrDefault(c => c.Name == sample.Catalog);
    if (catalog is null) return "";

    var lines = new List<string>();
    foreach (var dataset in catalog.Datasets)
        lines.Add($"- {dataset.Description} ({dataset.Plural}): " +
                  string.Join(", ", dataset.Columns.Select(c => c.Label)));

    return string.Join("\n", lines);
}

// Ayırt edici belirteçler: rakam içerenler (eşik, yıl, kod) ve büyük harfle başlayanlar
// (Ankara, Kurumsal). Küçük harfli sıradan kelimeler serbestçe değiştirilebilir —
// zaten dil çeşitliliği için parafraz yaptırıyoruz.
static IEnumerable<string> SignificantTokens(string text) =>
    text.Split(new[] { ' ', ',', ';', '(', ')', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.Trim('.', '?', ':'))
        .Where(t => t.Length >= 2)
        .Where(t => t.Any(char.IsDigit) || char.IsUpper(t[0]));

// ============================ build ============================

static int Build(Dictionary<string, string> options)
{
    var outDir = options.GetValueOrDefault("out", "data");

    var paraphrased = Path.Combine(outDir, "samples.train.para.jsonl");
    var trainSource = File.Exists(paraphrased)
        ? paraphrased
        : Path.Combine(outDir, "samples.train.jsonl");

    Console.WriteLine($"Eğitim kaynağı: {Path.GetFileName(trainSource)}");

    var written = 0;
    written += BuildFile(trainSource, Path.Combine(outDir, "train.jsonl"));
    written += BuildFile(Path.Combine(outDir, "samples.eval.jsonl"), Path.Combine(outDir, "eval.jsonl"));

    Console.WriteLine($"Toplam {written} satır yazıldı.");
    return 0;
}

static int BuildFile(string inputPath, string outputPath)
{
    var catalogCache = CatalogDefs.All.ToDictionary(c => c.Name, c => c.ToCatalog());

    using var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(false));
    var count = 0;

    foreach (var sample in ReadJsonl(inputPath))
    {
        // İstem, canlıda çalışan sınıfın ta kendisiyle kuruluyor — ÖRNEKSİZ biçimde,
        // çünkü ince ayarlı model few-shot örnek görmeyecek (bkz. QueryPlannerService).
        var prompt = QueryPromptBuilder.Build(
            sample.Question, catalogCache[sample.Catalog], includeExamples: false);

        writer.WriteLine(JsonSerializer.Serialize(new TrainingRow(
            prompt, sample.Plan, sample.Question, sample.Recipe, sample.Catalog,
            sample.Split, sample.Source), Jsonl));

        count++;
    }

    Console.WriteLine($"  {Path.GetFileName(outputPath)}: {count} satır");
    return count;
}

// ============================ evaluate ============================

static async Task<int> Evaluate(Dictionary<string, string> options)
{
    var inputPath = options.GetValueOrDefault("in", Path.Combine("data", "samples.eval.jsonl"));
    var model = options.GetValueOrDefault("model", "qwen2.5-coder:7b");
    var limit = int.Parse(options.GetValueOrDefault("limit", "0"));

    var catalogCache = CatalogDefs.All.ToDictionary(c => c.Name, c => c.ToCatalog());

    // İstem BURADA kuruluyor, dosyadan okunmuyor. Sebep: doğru karşılaştırma her modelin
    // KENDİ canlı istemiyle yapılmalı. Temel model bugün örnekleri görüyor; ince ayarlı
    // model görmeyecek. Tek bir istem biçimi dayatsaydık ya temel modeli haksız yere
    // zayıflatır ya da ince ayarın hız kazancını ölçemez hâle gelirdik.
    var withExamples = !model.StartsWith("veriyonetim", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"Model: {model} — istem {(withExamples ? "ÖRNEKLİ" : "ÖRNEKSİZ")}");

    var rows = ReadJsonl(inputPath)
        .Select(s => new TrainingRow(
            QueryPromptBuilder.Build(s.Question, catalogCache[s.Catalog], withExamples),
            s.Plan, s.Question, s.Recipe, s.Catalog, s.Split, s.Source))
        .ToList();

    if (limit > 0) rows = rows.Take(limit).ToList();

    var ollama = new Ollama(model);

    var results = new List<EvalResult>(rows.Count);
    var started = DateTime.Now;

    for (var i = 0; i < rows.Count; i++)
    {
        var row = rows[i];

        string raw;
        try
        {
            raw = await ollama.GenerateAsync(row.Prompt);
        }
        catch (Exception ex)
        {
            results.Add(new EvalResult(row, "", false, false, false, false, ex.Message));
            continue;
        }

        var (parsed, valid, exact, sameQuery, note) = Score(row, raw, catalogCache[row.Catalog]);
        results.Add(new EvalResult(row, raw, parsed, valid, exact, sameQuery, note));

        if ((i + 1) % 10 == 0)
        {
            var elapsed = DateTime.Now - started;
            var rate = (i + 1) / Math.Max(elapsed.TotalSeconds, 1);
            Console.WriteLine(
                $"{i + 1}/{rows.Count} — doğru {results.Count(r => r.Exact)} " +
                $"(kalan ~{TimeSpan.FromSeconds((rows.Count - i - 1) / Math.Max(rate, 0.0001)):hh\\:mm})");
        }
    }

    Report(model, results);

    var reportPath = Path.ChangeExtension(inputPath, null) + $".{Sanitize(model)}.sonuc.jsonl";
    using (var writer = new StreamWriter(reportPath, false, new UTF8Encoding(false)))
        foreach (var result in results)
            writer.WriteLine(JsonSerializer.Serialize(new SavedResult(
                result.Row.Question, result.Row.Split, result.Row.Recipe,
                result.Row.Completion, result.Raw, result.Valid, result.Exact,
                result.SameQuery, result.Note), Jsonl));

    Console.WriteLine($"Ayrıntılı sonuç: {reportPath}");
    return 0;
}

// Üç ayrı ölçüm: ayrıştırılabildi mi, çalıştırılabilir mi, DOĞRU sorgu mu.
// Aradaki fark önemli — çalışan ama yanlış soruyu cevaplayan bir plan, hata verenden
// daha tehlikelidir (kullanıcı yanlış sayıya inanır).
static (bool Parsed, bool Valid, bool Exact, bool SameQuery, string Note) Score(
    TrainingRow row, string raw, TenantCatalog catalog)
{
    JsonObject? node;
    try
    {
        node = JsonNode.Parse(raw)?.AsObject();
    }
    catch (JsonException ex)
    {
        return (false, false, false, false, $"JSON değil: {ex.Message}");
    }

    if (node is null) return (false, false, false, false, "Boş yanıt.");

    var error = PlanValidator.Validate(row.Question, node, catalog);
    if (error is not null) return (true, false, false, false, error);

    var produced = QueryPlanJson.Parse(PlanValidator.ToJson(node));
    var expected = QueryPlanJson.Parse(row.Completion);
    if (produced is null || expected is null) return (true, true, false, false, "Plan okunamadı.");

    var exact = PlanValidator.Canonical(produced, catalog) == PlanValidator.Canonical(expected, catalog);

    // Select hariç karşılaştırma: soru gösterilecek kolonu söylemiyorsa modelin başka
    // bir kolon seçmesi hata değildir (bkz. PlanValidator.Canonical).
    var sameQuery = exact || PlanValidator.Canonical(produced, catalog, includeSelect: false)
                          == PlanValidator.Canonical(expected, catalog, includeSelect: false);

    var note = exact ? "" : sameQuery ? "Aynı sorgu, farklı kolonlar." : "Farklı sorgu.";
    return (true, true, exact, sameQuery, note);
}

static void Report(string model, IReadOnlyList<EvalResult> results)
{
    Console.WriteLine();
    Console.WriteLine($"=== {model} ===");
    Console.WriteLine(
        $"{"Bölüm",-16}{"Adet",6}{"Ayrıştı",10}{"Geçerli",10}{"Doğru",10}{"Sorgu",10}");

    foreach (var group in results.GroupBy(r => r.Row.Split).OrderBy(g => g.Key))
        WriteLineFor(group.Key, group.ToList());

    WriteLineFor("TOPLAM", results);

    Console.WriteLine();
    Console.WriteLine("En çok hata veren tarifler (sorgunun kendisi yanlış olanlar):");
    foreach (var group in results.Where(r => !r.SameQuery)
                 .GroupBy(r => r.Row.Recipe)
                 .OrderByDescending(g => g.Count())
                 .Take(10))
        Console.WriteLine($"  {group.Count(),4}  {group.Key,-24} {group.First().Note}");

    static void WriteLineFor(string label, IReadOnlyList<EvalResult> rows)
    {
        if (rows.Count == 0) return;
        Console.WriteLine(
            $"{label,-16}{rows.Count,6}" +
            $"{Percent(rows.Count(r => r.Parsed), rows.Count),10}" +
            $"{Percent(rows.Count(r => r.Valid), rows.Count),10}" +
            $"{Percent(rows.Count(r => r.Exact), rows.Count),10}" +
            $"{Percent(rows.Count(r => r.SameQuery), rows.Count),10}");
    }

    static string Percent(int part, int total) => $"%{100.0 * part / total:0.0}";
}

// ============================ rescore ============================

// Kaydedilmiş bir ölçüm sonucunu, modeli yeniden çalıştırmadan yeniden puanlar.
//
// Puanlama ölçütü değişebiliyor (ör. nitelikli/sade kolon adlarının aynı sayılması).
// Her değişiklikte saatlerce süren üretimi tekrarlamak yerine ham yanıtlar saklanıyor
// ve yalnız karşılaştırma yeniden yapılıyor.
static int Rescore(Dictionary<string, string> options)
{
    var resultPath = options.GetValueOrDefault("in", "");
    var samplesPath = options.GetValueOrDefault("samples", Path.Combine("data", "samples.eval.jsonl"));

    if (!File.Exists(resultPath))
    {
        Console.WriteLine($"Sonuç dosyası yok: {resultPath}");
        return 1;
    }

    var catalogCache = CatalogDefs.All.ToDictionary(c => c.Name, c => c.ToCatalog());

    // Katalog/tarif/bölüm bilgisi sonuç dosyasında yok; soru metni üzerinden eşleniyor.
    var bySample = ReadJsonl(samplesPath)
        .GroupBy(s => Normalize(s.Question))
        .ToDictionary(g => g.Key, g => g.First());

    var results = new List<EvalResult>();
    var unmatched = 0;

    foreach (var line in File.ReadLines(resultPath))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;

        var saved = JsonSerializer.Deserialize<SavedResult>(line, Jsonl);
        if (saved is null) continue;

        if (!bySample.TryGetValue(Normalize(saved.Soru), out var sample))
        {
            unmatched++;
            continue;
        }

        var row = new TrainingRow(
            "", sample.Plan, sample.Question, sample.Recipe, sample.Catalog, sample.Split, sample.Source);

        var (parsed, valid, exact, sameQuery, note) = Score(row, saved.Uretilen, catalogCache[sample.Catalog]);
        results.Add(new EvalResult(row, saved.Uretilen, parsed, valid, exact, sameQuery, note));
    }

    if (unmatched > 0)
        Console.WriteLine($"Eşleşmeyen {unmatched} satır atlandı.");

    Report(Path.GetFileName(resultPath), results);
    return 0;
}

// ============================ ortak ============================

static Dictionary<string, string> ParseArgs(IEnumerable<string> args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? key = null;

    foreach (var arg in args)
    {
        if (arg.StartsWith("--"))
        {
            key = arg[2..];
            result[key] = "true";
        }
        else if (key is not null)
        {
            result[key] = arg;
            key = null;
        }
    }

    return result;
}

static string Normalize(string text) =>
    string.Join(' ', text.ToLowerInvariant().Split(
        new[] { ' ', '\t', '\n', '?', '.', ',', '!' }, StringSplitOptions.RemoveEmptyEntries));

static string Sanitize(string text) =>
    string.Concat(text.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));

static void Shuffle<T>(IList<T> items, Random rnd)
{
    for (var i = items.Count - 1; i > 0; i--)
    {
        var j = rnd.Next(i + 1);
        (items[i], items[j]) = (items[j], items[i]);
    }
}

static void WriteJsonl(string path, IEnumerable<SampleRecord> samples)
{
    using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
    foreach (var sample in samples)
        writer.WriteLine(JsonSerializer.Serialize(sample, Jsonl));
}

static IEnumerable<SampleRecord> ReadJsonl(string path)
{
    foreach (var line in File.ReadLines(path))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var record = JsonSerializer.Deserialize<SampleRecord>(line, Jsonl);
        if (record is not null) yield return record;
    }
}
