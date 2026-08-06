using System.Text.Json.Nodes;

namespace VeriYonetim.TrainingData;

// Tek bir üretim sonucu: soru + o sorunun DOĞRU planı.
public record Draft(string Question, JsonObject Plan);

// Bir soru tipi. Make, katalog uygun değilse null döner (her katalogda her kolon tipi
// bulunmaz); üretici o denemeyi atlar.
public record Recipe(string Name, Func<Gen, CatalogDef, Draft?> Make);

// Rastgele seçim + katalogdan tip/role göre kolon çekme.
public sealed class Gen(Random rnd)
{
    public T Pick<T>(IReadOnlyList<T> items) => items[rnd.Next(items.Count)];
    public bool Chance(double p) => rnd.NextDouble() < p;
    public int Int(int min, int max) => rnd.Next(min, max + 1);

    public IReadOnlyList<T> Sample<T>(IReadOnlyList<T> items, int count) =>
        items.OrderBy(_ => rnd.Next()).Take(count).ToList();

    public DatasetDef Ds(CatalogDef c) => Pick(c.Datasets);

    // Ad niteliğindeki kolonlar. Kendi setinde her satır için farklıdırlar (her personelin
    // adı kendine aittir), yani KENDİ setinde gruplamak kimliğe göre gruplamakla aynı
    // anlamsızlığa düşer: "ortalama maaşı 25000 üstü olan ad soyad hangileri" gibi.
    //
    // Ama BAŞKA bir setten bakıldığında aynı kolon gayet anlamlıdır — satışları müşteri
    // adına göre toplamak gerçek bir sorudur. Bu yüzden tümden yasaklanmıyor: tek setli
    // tariflerde dışarıda, JOIN'li tariflerde içeride (bkz. DimOrName).
    private static readonly HashSet<string> NameLike =
        new() { "ad", "ad_soyad", "unvan", "urun_adi", "surucu", "plaka" };

    // Boyut kolonu: gruplanabilir metin. Kimlikler ve ad kolonları dışarıda.
    public ColumnDef? Dim(DatasetDef d) => Opt(Dimensions(d));

    // Ad kolonlarını da kabul eden biçim: yalnız JOIN'li tarifler kullanır.
    public ColumnDef? DimOrName(DatasetDef d) =>
        Opt(d.Columns.Where(c => c.Type == "text" && !c.IsId));
    public ColumnDef? Num(DatasetDef d) => Opt(d.Columns.Where(c => c.Type == "number"));
    public ColumnDef? Date(DatasetDef d) => Opt(d.Columns.Where(c => c.Type == "date"));
    public ColumnDef? Ident(DatasetDef d) => Opt(d.Columns.Where(c => c.IsId));

    // "Adı ne" sorularının cevabı olan kolon; yoksa herhangi bir boyut kolonu.
    public ColumnDef? NameCol(DatasetDef d)
    {
        var preferred = d.Columns
            .Where(c => c.Name is "ad" or "unvan" or "ad_soyad" or "urun_adi" or "surucu")
            .ToList();
        return preferred.Count > 0 ? Pick(preferred) : Dim(d);
    }

    public IReadOnlyList<ColumnDef> Dims(DatasetDef d) => Dimensions(d).ToList();

    private static IEnumerable<ColumnDef> Dimensions(DatasetDef d) =>
        d.Columns.Where(c => c.Type == "text" && !c.IsId && !NameLike.Contains(c.Name));

    public string Val(ColumnDef c) => c.Values.Length == 0 ? "" : Pick(c.Values);

    private ColumnDef? Opt(IEnumerable<ColumnDef> source)
    {
        var list = source.ToList();
        return list.Count == 0 ? null : Pick(list);
    }
}

// Plan JSON'unu parça parça kuran yardımcılar.
//
// Neden QueryPlan kaydı değil de JsonObject? Çünkü modele öğretilen şey tam olarak bu
// metin. "Kullanmadığın alanları hiç yazma" kuralına uymak için hangi alanın yazılıp
// hangisinin yazılmayacağını tek tek denetlemek gerekiyor; kayıttan serileştirmede
// varsayılan değerler (share:false, sortMetric:0) istemsizce çıktıya sızardı.
public static class P
{
    public static JsonObject Rows(string from) => new() { ["kind"] = "rows", ["from"] = from };
    public static JsonObject Agg(string from) => new() { ["kind"] = "aggregate", ["from"] = from };

    public static JsonObject Unsupported(string reason) =>
        new() { ["kind"] = "unsupported", ["reason"] = reason };

    public static JsonArray Arr(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    public static JsonObject Metric(string op, string? column = null)
    {
        var metric = new JsonObject { ["op"] = op };
        if (column is not null) metric["column"] = column;
        return metric;
    }

    public static JsonObject Leaf(string column, string op, string? value = null)
    {
        var leaf = new JsonObject { ["column"] = column, ["op"] = op };
        if (value is not null) leaf["value"] = value;
        return leaf;
    }

    public static JsonObject Many(string column, string op, IEnumerable<string> values)
    {
        var node = new JsonObject { ["column"] = column, ["op"] = op };
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        node["values"] = array;
        return node;
    }

    public static JsonObject Or(params JsonObject[] children)
    {
        var array = new JsonArray();
        foreach (var child in children) array.Add(child);
        return new JsonObject { ["logic"] = "or", ["children"] = array };
    }

    // Filtreleri plana ekler. Sıra korunuyor: modelin öğrendiği çıktı deterministik olmalı.
    public static JsonObject With(this JsonObject plan, params JsonObject[] filters)
    {
        var array = new JsonArray();
        foreach (var filter in filters) array.Add(filter);
        plan["filters"] = array;
        return plan;
    }
}

public static class Recipes
{
    // Göreli dönem etiketi → soruda geçecek Türkçe karşılığı.
    private static readonly (string Token, string Phrase)[] Periods =
    {
        ("bugun", "bugün"), ("dun", "dün"),
        ("buHafta", "bu hafta"), ("gecenHafta", "geçen hafta"),
        ("son7Gun", "son 7 günde"), ("son30Gun", "son 30 günde"), ("son90Gun", "son 90 günde"),
        ("buAy", "bu ay"), ("gecenAy", "geçen ay"), ("son12Ay", "son 12 ayda"),
        ("buCeyrek", "bu çeyrek"), ("gecenCeyrek", "geçen çeyrek"),
        ("buYil", "bu yıl"), ("gecenYil", "geçen yıl")
    };

    // "A, B ve C" — Türkçe liste noktalaması. "A ve B ve C" yazmak cümleyi bozuyordu.
    private static string Join(IReadOnlyList<string> values) =>
        values.Count <= 1
            ? string.Concat(values)
            : string.Join(", ", values.Take(values.Count - 1)) + " ve " + values[^1];

    private static readonly (string Bucket, string Phrase)[] Buckets =
    {
        ("day", "günlere göre"), ("week", "haftalara göre"),
        ("month", "aylara göre"), ("year", "yıllara göre")
    };

    public static readonly IReadOnlyList<Recipe> All = new Recipe[]
    {
        // ===================== SATIR LİSTESİ (kind=rows) =====================

        new("rows_top_n", (g, c) =>
        {
            var ds = g.Ds(c);
            var num = g.Num(ds); var dim = g.Dim(ds);
            if (num is null || dim is null) return null;

            var n = g.Pick(new[] { 3, 5, 10 });
            var desc = g.Chance(0.7);

            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(dim.Name, num.Name);
            plan["sort"] = num.Name;
            plan["dir"] = desc ? "desc" : "asc";
            plan["limit"] = n;

            var question = desc
                ? g.Pick(new[]
                {
                    $"{num.Label} bakımından en yüksek {n} {ds.Singular}",
                    $"en çok {num.Label} olan ilk {n} {ds.Singular}",
                    $"{num.Label} en yüksek {n} {ds.Singular} hangileri"
                })
                : g.Pick(new[]
                {
                    $"{num.Label} bakımından en düşük {n} {ds.Singular}",
                    $"en az {num.Label} olan ilk {n} {ds.Singular}"
                });

            return new Draft(question, plan);
        }),

        new("rows_latest", (g, c) =>
        {
            var ds = g.Ds(c);
            var date = g.Date(ds);
            if (date is null) return null;

            var newest = g.Chance(0.7);

            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(date.Name);
            plan["sort"] = date.Name;
            plan["dir"] = newest ? "desc" : "asc";
            plan["limit"] = 1;

            var question = newest
                ? g.Pick(new[]
                {
                    $"en son {ds.Singular} ne zaman",
                    $"son {ds.Singular} hangi tarihte",
                    $"{ds.Genitive} en yenisi ne zaman",
                    $"en güncel {ds.Singular} kaydının {date.LabelPoss} nedir",
                    $"{ds.Plural} en son ne zaman güncellendi"
                })
                : g.Pick(new[]
                {
                    $"ilk {ds.Singular} ne zaman",
                    $"en eski {ds.Singular} hangi tarihte",
                    $"{ds.Genitive} en eskisi hangi tarihli",
                    $"ilk kayıt {date.LabelPoss} nedir"
                });

            return new Draft(question, plan);
        }),

        new("rows_filter_eq", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null || num is null) return null;

            var value = g.Val(dim);
            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(dim.Name, num.Name);
            plan.With(P.Leaf(dim.Name, "eq", value));

            var question = g.Pick(new[]
            {
                $"{dim.LabelPoss} {value} olan {ds.Plural}",
                $"{value} {dim.LabelPoss} olan kayıtları listele",
                $"{dim.LabelPoss} {value} olan {ds.Plural} neler"
            });

            return new Draft(question, plan);
        }),

        new("rows_filter_in", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null || num is null || dim.Values.Length < 2) return null;

            var values = g.Sample(dim.Values, g.Int(2, Math.Min(3, dim.Values.Length)));
            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(dim.Name, num.Name);
            plan.With(P.Many(dim.Name, "in", values));

            var joined = Join(values);
            var question = g.Pick(new[]
            {
                $"{dim.LabelPoss} {joined} olan {ds.Plural}",
                $"{joined} {dim.LabelPoss} olan {ds.Genitive} listesi"
            });

            return new Draft(question, plan);
        }),

        new("rows_contains", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds);
            if (dim is null || dim.Values.Length == 0) return null;

            var value = g.Val(dim);
            // Değerin bir parçası aranıyor: "contains" tam eşleşme değil, içinde geçme.
            var fragment = value.Length > 4 ? value[..g.Int(3, Math.Min(5, value.Length))] : value;

            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(dim.Name);
            plan.With(P.Leaf(dim.Name, "contains", fragment));

            var question = g.Pick(new[]
            {
                $"{dim.LabelPoss} içinde \"{fragment}\" geçen {ds.Plural}",
                $"{dim.Label} alanında \"{fragment}\" geçen kayıtlar"
            });

            return new Draft(question, plan);
        }),

        new("rows_compare_number", (g, c) =>
        {
            var ds = g.Ds(c);
            var num = g.Num(ds); var dim = g.Dim(ds);
            if (num is null || dim is null || num.Values.Length == 0) return null;

            var value = g.Val(num);
            var op = g.Pick(new[] { "gt", "gte", "lt", "lte" });

            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(dim.Name, num.Name);
            plan.With(P.Leaf(num.Name, op, value));

            // Cümle operatöre BAĞLI seçiliyor. Aynı cümleye bir seferinde gt, bir
            // seferinde gte üretmek modele "sınır dahil mi değil mi fark etmez" diye
            // öğretirdi; oysa "1000 üzerinde" ile "en az 1000" farklı sorulardır.
            var question = op switch
            {
                "gt" => g.Pick(new[]
                {
                    $"{num.LabelPoss} {value} üzerinde olan {ds.Plural}",
                    $"{num.Label} {value} değerini aşan {ds.Plural}",
                    $"{value} üstü {num.Label} olan {ds.Plural}"
                }),
                "gte" => g.Pick(new[]
                {
                    $"{num.LabelPoss} en az {value} olan {ds.Plural}",
                    $"{num.Label} {value} ve üzeri olan {ds.Plural}"
                }),
                "lt" => g.Pick(new[]
                {
                    $"{num.LabelPoss} {value} altında olan {ds.Plural}",
                    $"{num.Label} {value} değerinin altındaki {ds.Plural}"
                }),
                _ => g.Pick(new[]
                {
                    $"{num.LabelPoss} en fazla {value} olan {ds.Plural}",
                    $"{num.Label} {value} ve altı olan {ds.Plural}"
                })
            };

            return new Draft(question, plan);
        }),

        new("rows_range", (g, c) =>
        {
            var ds = g.Ds(c);
            var num = g.Num(ds); var dim = g.Dim(ds);
            if (num is null || dim is null || num.Values.Length < 2) return null;

            var picked = g.Sample(num.Values, 2).Select(decimal.Parse).OrderBy(v => v).ToList();
            var low = picked[0];
            var high = picked[1];
            if (low == high) return null;

            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(dim.Name, num.Name);
            plan.With(
                P.Leaf(num.Name, "gte", low.ToString()),
                P.Leaf(num.Name, "lte", high.ToString()));

            var question = g.Pick(new[]
            {
                $"{num.LabelPoss} {low} ile {high} arasında olan {ds.Plural}",
                $"{num.Label} {low}-{high} aralığındaki {ds.Plural}"
            });

            return new Draft(question, plan);
        }),

        new("rows_isnull", (g, c) =>
        {
            var ds = g.Ds(c);
            var target = g.Chance(0.5) ? g.Num(ds) : g.Dim(ds);
            if (target is null) return null;

            var missing = g.Chance(0.7);
            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(target.Name);
            plan.With(P.Leaf(target.Name, missing ? "isNull" : "notNull"));

            var question = missing
                ? g.Pick(new[]
                {
                    $"{target.LabelPoss} girilmemiş {ds.Plural}",
                    $"{target.Label} bilgisi boş olan kayıtlar",
                    $"{target.LabelPoss} eksik olan {ds.Plural}"
                })
                : g.Pick(new[]
                {
                    $"{target.LabelPoss} dolu olan {ds.Plural}",
                    $"{target.Label} bilgisi girilmiş kayıtlar"
                });

            return new Draft(question, plan);
        }),

        new("rows_period", (g, c) =>
        {
            var ds = g.Ds(c);
            var date = g.Date(ds); var dim = g.Dim(ds); var num = g.Num(ds);
            if (date is null || dim is null || num is null) return null;

            var (token, phrase) = g.Pick(Periods);

            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(dim.Name, num.Name);
            plan.With(P.Leaf(date.Name, "inPeriod", token));

            // Fiil, veri setinden BAĞIMSIZ seçilmeli: "yapılan satışlar" doğru ama
            // "yapılan personeller" saçma. Her sete uyan yüklemlerle sınırlı kalınıyor.
            var question = g.Pick(new[]
            {
                $"{phrase} kaydedilen {ds.Plural}",
                $"{phrase} kaydedilen {ds.Plural} listesi",
                $"{phrase} {ds.Genitive} listesi",
                $"{phrase} girilen {ds.Plural}",
                $"{phrase} eklenen {ds.Plural}"
            });

            return new Draft(question, plan);
        }),

        new("rows_year", (g, c) =>
        {
            var ds = g.Ds(c);
            var date = g.Date(ds); var dim = g.Dim(ds); var num = g.Num(ds);
            if (date is null || dim is null || num is null) return null;

            var year = g.Int(2021, 2025);

            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(dim.Name, num.Name);
            plan.With(
                P.Leaf(date.Name, "gte", $"{year}-01-01"),
                P.Leaf(date.Name, "lt", $"{year + 1}-01-01"));

            var question = g.Pick(new[]
            {
                $"{year} yılındaki {ds.Plural}",
                $"{year} yılında kaydedilen {ds.Plural}"
            });

            return new Draft(question, plan);
        }),

        new("rows_id_lookup", (g, c) =>
        {
            var ds = g.Ds(c);
            var id = g.Ident(ds); var num = g.Num(ds);
            if (id is null || num is null || id.Values.Length == 0) return null;

            var value = g.Val(id);

            var plan = P.Rows(ds.Name);
            plan["select"] = P.Arr(num.Name);
            plan["limit"] = 1;
            plan.With(P.Leaf(id.Name, "eq", value));

            var question = g.Pick(new[]
            {
                $"{value} {id.Label} kaydının {num.LabelPoss} nedir",
                $"{id.LabelPoss} {value} olan kaydın {num.Label} bilgisi"
            });

            return new Draft(question, plan);
        }),

        new("rows_extreme_name", (g, c) =>
        {
            var ds = g.Ds(c);
            var num = g.Num(ds); var name = g.NameCol(ds);
            if (num is null || name is null) return null;

            var highest = g.Chance(0.7);

            var plan = P.Rows(ds.Name);
            // Yalnız sorulan kolon: "kimin" sorusunun cevabı tek bir isimdir, o satırın
            // bütün alanları değil.
            plan["select"] = P.Arr(name.Name);
            plan["sort"] = num.Name;
            plan["dir"] = highest ? "desc" : "asc";
            plan["limit"] = 1;

            var question = highest
                ? g.Pick(new[]
                {
                    $"en yüksek {num.Label} hangi {name.Label} kaydında",
                    $"{num.Label} en yüksek olan kaydın {name.LabelPoss} nedir"
                })
                : g.Pick(new[]
                {
                    $"en düşük {num.Label} hangi {name.Label} kaydında",
                    $"{num.Label} en düşük olan kaydın {name.LabelPoss} nedir"
                });

            return new Draft(question, plan);
        }),

        // ===================== ÖZET, GRUPLAMASIZ =====================

        new("agg_count", (g, c) =>
        {
            var ds = g.Ds(c);
            var plan = P.Agg(ds.Name);
            plan["metrics"] = new JsonArray { P.Metric("count") };

            var question = g.Pick(new[]
            {
                $"kaç {ds.Singular} var",
                $"toplam {ds.Singular} sayısı nedir",
                $"{ds.Plural} kaç kayıt",
                $"{ds.Genitive} adedi ne kadar",
                $"sistemde kaç {ds.Singular} kayıtlı",
                $"{ds.Singular} sayısını göster",
                $"toplam kaç {ds.Singular} kaydı bulunuyor"
            });

            return new Draft(question, plan);
        }),

        new("agg_simple_metric", (g, c) =>
        {
            var ds = g.Ds(c);
            var num = g.Num(ds);
            if (num is null) return null;

            var (op, phrases) = g.Pick(new (string, string[])[]
            {
                ("sum", new[] { $"toplam {num.Label} ne kadar", $"{num.Label} toplamı nedir", $"{ds.Genitive} toplam {num.LabelPoss}" }),
                ("avg", new[] { $"ortalama {num.Label} nedir", $"{num.Label} ortalaması ne kadar" }),
                ("max", new[] { $"en yüksek {num.Label} nedir", $"{num.Label} en fazla ne kadar" }),
                ("min", new[] { $"en düşük {num.Label} nedir", $"{num.Label} en az ne kadar" }),
                // Medyan: ortadaki değer. Ortalamayla karıştırılmaması gereken ayrı bir soru.
                ("median", new[] { $"medyan {num.Label} nedir", $"{num.Label} medyanı ne kadar", $"tipik {num.Label} nedir", $"ortanca {num.Label} nedir" })
            });

            var plan = P.Agg(ds.Name);
            plan["metrics"] = new JsonArray { P.Metric(op, num.Name) };

            return new Draft(g.Pick(phrases), plan);
        }),

        new("agg_distinct", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds);
            if (dim is null) return null;

            var plan = P.Agg(ds.Name);
            plan["metrics"] = new JsonArray { P.Metric("countDistinct", dim.Name) };

            var question = g.Pick(new[]
            {
                $"kaç farklı {dim.Label} var",
                $"kaç değişik {dim.Label} kaydedilmiş",
                $"benzersiz {dim.Label} sayısı nedir"
            });

            return new Draft(question, plan);
        }),

        new("agg_filtered", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null || num is null) return null;

            var value = g.Val(dim);
            var op = g.Pick(new[] { "count", "sum", "avg" });

            var plan = P.Agg(ds.Name);
            plan["metrics"] = new JsonArray { op == "count" ? P.Metric("count") : P.Metric(op, num.Name) };
            plan.With(P.Leaf(dim.Name, "eq", value));

            var question = op switch
            {
                "count" => g.Pick(new[]
                {
                    $"{dim.LabelPoss} {value} olan kaç {ds.Singular} var",
                    $"{dim.LabelPoss} {value} olan kaç {ds.Singular} kaydı var"
                }),
                "sum" => $"{dim.LabelPoss} {value} olan {ds.Genitive} toplam {num.LabelPoss}",
                _ => $"{dim.LabelPoss} {value} olan {ds.Genitive} ortalama {num.LabelPoss}"
            };

            return new Draft(question, plan);
        }),

        new("agg_not_equal", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds);
            if (dim is null || dim.Values.Length < 2) return null;

            var excludeMany = g.Chance(0.5) && dim.Values.Length >= 3;

            var plan = P.Agg(ds.Name);
            plan["metrics"] = new JsonArray { P.Metric("count") };

            string question;
            if (excludeMany)
            {
                var values = g.Sample(dim.Values, 2);
                plan.With(P.Many(dim.Name, "notIn", values));
                question = $"{dim.LabelPoss} {Join(values)} dışında olan kayıtların adedi";
            }
            else
            {
                var value = g.Val(dim);
                plan.With(P.Leaf(dim.Name, "ne", value));
                question = g.Pick(new[]
                {
                    $"{dim.LabelPoss} {value} olmayan kaç {ds.Singular} var",
                    $"{value} dışındaki {ds.Genitive} adedi"
                });
            }

            return new Draft(question, plan);
        }),

        new("agg_period", (g, c) =>
        {
            var ds = g.Ds(c);
            var date = g.Date(ds); var num = g.Num(ds);
            if (date is null || num is null) return null;

            var (token, phrase) = g.Pick(Periods);
            var op = g.Pick(new[] { "sum", "count", "avg" });

            var plan = P.Agg(ds.Name);
            plan["metrics"] = new JsonArray { op == "count" ? P.Metric("count") : P.Metric(op, num.Name) };
            plan.With(P.Leaf(date.Name, "inPeriod", token));

            var question = op switch
            {
                "count" => $"{phrase} kaç {ds.Singular} var",
                "sum" => g.Pick(new[] { $"{phrase} toplam {num.Label}", $"{phrase} {num.Label} toplamı ne kadar" }),
                _ => $"{phrase} ortalama {num.Label}"
            };

            return new Draft(question, plan);
        }),

        new("agg_year", (g, c) =>
        {
            var ds = g.Ds(c);
            var date = g.Date(ds); var num = g.Num(ds);
            if (date is null || num is null) return null;

            var year = g.Int(2021, 2025);
            var op = g.Pick(new[] { "sum", "count" });

            var plan = P.Agg(ds.Name);
            plan["metrics"] = new JsonArray { op == "count" ? P.Metric("count") : P.Metric("sum", num.Name) };
            // Mutlak yıl için inPeriod YOK: iki sınır filtresi. Yarı açık aralık
            // [yıl-01-01, sonraki yıl-01-01) — 31 Aralık saat 14:00 kaydı da içeride kalsın.
            plan.With(
                P.Leaf(date.Name, "gte", $"{year}-01-01"),
                P.Leaf(date.Name, "lt", $"{year + 1}-01-01"));

            var question = op == "count"
                ? $"{year} yılında kaç {ds.Singular} var"
                : g.Pick(new[]
                {
                    $"{year} yılında toplam {num.Label} ne kadar",
                    $"{year} yılı {num.Label} toplamı"
                });

            return new Draft(question, plan);
        }),

        new("agg_or", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null || num is null || num.Values.Length == 0) return null;

            var value = g.Val(dim);
            var threshold = g.Val(num);

            var plan = P.Agg(ds.Name);
            plan["metrics"] = new JsonArray { P.Metric("count") };
            // VEYA ağacı: düz listede filtreler VE ile bağlanır, bu soru öyle kurulamaz.
            plan.With(P.Or(
                P.Leaf(dim.Name, "eq", value),
                P.Leaf(num.Name, "gt", threshold)));

            var question = g.Pick(new[]
            {
                $"{dim.LabelPoss} {value} olan veya {num.LabelPoss} {threshold} üzerinde olan kaç {ds.Singular} var",
                $"{value} {dim.Label} ya da {threshold} üstü {num.Label} kayıtlarının sayısı"
            });

            return new Draft(question, plan);
        }),

        // ===================== ÖZET, GRUPLAMALI =====================

        new("group_metric", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null) return null;

            var op = num is null ? "count" : g.Pick(new[] { "sum", "avg", "count", "max", "min", "median" });
            if (op != "count" && num is null) return null;

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(dim.Name);
            plan["metrics"] = new JsonArray { op == "count" ? P.Metric("count") : P.Metric(op, num!.Name) };

            var question = op switch
            {
                "count" => g.Pick(new[]
                {
                    $"{dim.ByPhrase} {ds.Singular} adedi",
                    $"{dim.ByPhrase} kaç {ds.Singular} var",
                    $"{dim.ByPhrase} kayıt sayısı"
                }),
                "sum" => g.Pick(new[]
                {
                    $"{dim.ByPhrase} toplam {num!.Label}",
                    $"{dim.ByPhrase} {num!.Label} toplamı",
                    $"{dim.ByPhrase} {ds.Genitive} toplam {num!.LabelPoss}"
                }),
                "avg" => g.Pick(new[]
                {
                    $"{dim.ByPhrase} ortalama {num!.Label}",
                    $"{dim.ByPhrase} {num!.Label} ortalaması"
                }),
                "max" => $"{dim.ByPhrase} en yüksek {num!.Label}",
                "min" => $"{dim.ByPhrase} en düşük {num!.Label}",
                _ => g.Pick(new[]
                {
                    $"{dim.ByPhrase} medyan {num!.Label}",
                    $"{dim.ByPhrase} ortanca {num!.Label}"
                })
            };

            return new Draft(question, plan);
        }),

        new("group_top_n", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null || num is null) return null;

            var n = g.Pick(new[] { 3, 5, 10 });
            var desc = g.Chance(0.8);

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(dim.Name);
            plan["metrics"] = new JsonArray { P.Metric("sum", num.Name) };
            // Gruplamalı sıralamada sort yalnız "key" veya "value" olabilir; kolon adı değil.
            plan["sort"] = "value";
            plan["dir"] = desc ? "desc" : "asc";
            plan["limit"] = n;

            var question = desc
                ? g.Pick(new[]
                {
                    $"en çok {num.Label} olan ilk {n} {dim.Label}",
                    $"{dim.ByPhrase} toplam {num.Label}, en yüksek {n} tanesi",
                    $"{num.Label} bakımından ilk {n} {dim.Label}"
                })
                : g.Pick(new[]
                {
                    $"en az {num.Label} olan {n} {dim.Label}",
                    $"{dim.ByPhrase} toplam {num.Label}, en düşük {n} tanesi"
                });

            return new Draft(question, plan);
        }),

        new("group_two_dims", (g, c) =>
        {
            var ds = g.Ds(c);
            var dims = g.Dims(ds);
            var num = g.Num(ds);
            if (dims.Count < 2 || num is null) return null;

            var pair = g.Sample(dims, 2);

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(pair[0].Name, pair[1].Name);
            plan["metrics"] = new JsonArray { P.Metric("sum", num.Name) };

            var question = g.Pick(new[]
            {
                $"{pair[0].ByPhrase} ve {pair[1].ByPhrase} toplam {num.Label}",
                $"{pair[0].Label} ve {pair[1].Label} kırılımında toplam {num.Label}",
                $"{pair[0].ByPhrase} toplam {num.Label}, {pair[1].Label} ayrımıyla"
            });

            return new Draft(question, plan);
        }),

        new("group_multi_metric", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null || num is null) return null;

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(dim.Name);
            plan["metrics"] = new JsonArray { P.Metric("count"), P.Metric("sum", num.Name) };

            var question = g.Pick(new[]
            {
                $"{dim.ByPhrase} kayıt adedi ve toplam {num.Label}",
                $"{dim.ByPhrase} hem {ds.Singular} sayısı hem {num.Label} toplamı"
            });

            return new Draft(question, plan);
        }),

        new("group_having", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null || num is null || num.Values.Length == 0) return null;

            var value = decimal.Parse(g.Val(num));
            var op = g.Chance(0.7) ? "gt" : "lt";

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(dim.Name);
            plan["metrics"] = new JsonArray { P.Metric("avg", num.Name) };
            // HAVING: koşul satırın değil GRUBUN ortalamasıyla ilgili, WHERE ile kurulamaz.
            plan["having"] = new JsonObject { ["metric"] = 0, ["op"] = op, ["value"] = value };

            var question = op == "gt"
                ? g.Pick(new[]
                {
                    $"ortalama {num.Label} {value} üzerinde olan {dim.Label} hangileri",
                    $"{dim.ByPhrase} ortalama {num.Label}, yalnızca {value} üstündekiler"
                })
                : $"ortalama {num.Label} {value} altında kalan {dim.Label} hangileri";

            return new Draft(question, plan);
        }),

        new("group_share", (g, c) =>
        {
            var ds = g.Ds(c);
            var dim = g.Dim(ds); var num = g.Num(ds);
            if (dim is null) return null;

            var useSum = num is not null && g.Chance(0.7);

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(dim.Name);
            // Pay yalnız count ve sum'da anlamlıdır: ortalamaların toplamı bir bütün etmez.
            plan["metrics"] = new JsonArray { useSum ? P.Metric("sum", num!.Name) : P.Metric("count") };
            plan["share"] = true;

            var question = useSum
                ? g.Pick(new[]
                {
                    $"{dim.ByPhrase} toplam {num!.Label} ve yüzde payları",
                    $"hangi {dim.Label} toplam {num!.Label} içinde ne kadar paya sahip",
                    $"{dim.Label} bazında {num!.Label} dağılımının yüzdesi"
                })
                : g.Pick(new[]
                {
                    $"{dim.ByPhrase} kayıt adedi ve yüzde dağılımı",
                    $"{dim.Label} bazında kayıtların yüzde dağılımı"
                });

            return new Draft(question, plan);
        }),

        new("group_filtered", (g, c) =>
        {
            var ds = g.Ds(c);
            var dims = g.Dims(ds);
            var num = g.Num(ds);
            if (dims.Count < 2 || num is null) return null;

            var pair = g.Sample(dims, 2);
            var value = g.Val(pair[1]);

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(pair[0].Name);
            plan["metrics"] = new JsonArray { P.Metric("sum", num.Name) };
            plan.With(P.Leaf(pair[1].Name, "eq", value));

            var question = g.Pick(new[]
            {
                $"yalnızca {value} {pair[1].LabelPoss} için {pair[0].ByPhrase} toplam {num.Label}",
                $"{pair[1].LabelPoss} {value} olan kayıtlarda {pair[0].ByPhrase} toplam {num.Label}"
            });

            return new Draft(question, plan);
        }),

        new("group_distinct", (g, c) =>
        {
            var ds = g.Ds(c);
            var dims = g.Dims(ds);
            if (dims.Count < 2) return null;

            var pair = g.Sample(dims, 2);

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(pair[0].Name);
            plan["metrics"] = new JsonArray { P.Metric("countDistinct", pair[1].Name) };

            var question = g.Pick(new[]
            {
                $"{pair[0].ByPhrase} kaç farklı {pair[1].Label} var",
                $"{pair[0].Label} başına düşen benzersiz {pair[1].Label} sayısı"
            });

            return new Draft(question, plan);
        }),

        // ===================== ZAMAN SERİSİ =====================

        new("time_bucket", (g, c) =>
        {
            var ds = g.Ds(c);
            var date = g.Date(ds); var num = g.Num(ds);
            if (date is null) return null;

            var (bucket, phrase) = g.Pick(Buckets);
            var useSum = num is not null && g.Chance(0.7);

            var plan = P.Agg(ds.Name);
            // bucket YALNIZCA ilk gruplama kolonuna uygulanır ve o kolon tarih olmalı.
            plan["groupBy"] = P.Arr(date.Name);
            plan["bucket"] = bucket;
            plan["metrics"] = new JsonArray { useSum ? P.Metric("sum", num!.Name) : P.Metric("count") };

            var question = useSum
                ? g.Pick(new[]
                {
                    $"{phrase} toplam {num!.Label}",
                    $"{phrase} {num!.Label} değişimi",
                    $"{phrase} {ds.Genitive} toplam {num!.LabelPoss}"
                })
                : g.Pick(new[]
                {
                    $"{phrase} {ds.Singular} adedi",
                    $"{phrase} kayıt sayısı"
                });

            return new Draft(question, plan);
        }),

        new("time_bucket_period", (g, c) =>
        {
            var ds = g.Ds(c);
            var date = g.Date(ds); var num = g.Num(ds);
            if (date is null || num is null) return null;

            // Kova ile dönem UYUMLU seçiliyor. "Son 30 günde aylara göre" diye bir soru
            // sorulmaz: pencere iki kova bile etmez. Uyumsuz çift teknik olarak geçerli
            // bir plan üretir, ama modele gerçek hayatta karşılaşmayacağı bir eşleme
            // öğretir ve doğal soruları o kalıba benzetmeye başlar.
            var (bucket, bucketPhrase, periodTokens) = g.Pick(new (string, string, string[])[]
            {
                ("day", "günlere göre",
                    new[] { "buHafta", "gecenHafta", "son7Gun", "son30Gun", "buAy", "gecenAy" }),
                ("week", "haftalara göre",
                    new[] { "son30Gun", "son90Gun", "buCeyrek", "gecenCeyrek", "buAy", "gecenAy" }),
                ("month", "aylara göre",
                    new[] { "son12Ay", "buYil", "gecenYil", "buCeyrek", "gecenCeyrek" })
            });

            var token = g.Pick(periodTokens);
            var periodPhrase = Periods.First(p => p.Token == token).Phrase;

            var plan = P.Agg(ds.Name);
            plan["groupBy"] = P.Arr(date.Name);
            plan["bucket"] = bucket;
            plan["metrics"] = new JsonArray { P.Metric("sum", num.Name) };
            plan.With(P.Leaf(date.Name, "inPeriod", token));

            var question = g.Pick(new[]
            {
                $"{periodPhrase} {bucketPhrase} toplam {num.Label}",
                $"{periodPhrase} {num.Label} nasıl seyretti, {bucketPhrase}"
            });

            return new Draft(question, plan);
        }),

        new("time_compare", (g, c) =>
        {
            var ds = g.Ds(c);
            var date = g.Date(ds); var num = g.Num(ds); var dim = g.Dim(ds);
            if (date is null || num is null) return null;

            var (current, previous, phrase) = g.Pick(new (string, string, string)[]
            {
                ("buAy", "gecenAy", "bu ay geçen aya göre"),
                ("buYil", "gecenYil", "bu yıl geçen yıla göre"),
                ("buCeyrek", "gecenCeyrek", "bu çeyrek geçen çeyreğe göre"),
                ("buHafta", "gecenHafta", "bu hafta geçen haftaya göre")
            });

            var grouped = dim is not null && g.Chance(0.7);

            var plan = P.Agg(ds.Name);
            if (grouped) plan["groupBy"] = P.Arr(dim!.Name);
            plan["metrics"] = new JsonArray { P.Metric("sum", num.Name) };
            plan["compare"] = new JsonObject
            {
                ["column"] = date.Name,
                ["period"] = current,
                ["previous"] = previous
            };

            var question = grouped
                ? g.Pick(new[]
                {
                    $"{phrase} {dim!.ByPhrase} {num.Label} nasıl",
                    $"{dim!.ByPhrase} {num.Label} {phrase} nasıl değişti"
                })
                : g.Pick(new[]
                {
                    $"{phrase} toplam {num.Label} nasıl",
                    $"toplam {num.Label} {phrase} ne durumda"
                });

            return new Draft(question, plan);
        }),

        // ===================== VERİ SETLERİ ARASI (join) =====================

        new("join_group", (g, c) =>
        {
            if (c.Relations.Length == 0) return null;

            var relation = g.Pick(c.Relations);
            var main = c.Datasets.First(d => d.Name == relation.FromDataset);
            var other = c.Datasets.First(d => d.Name == relation.ToDataset);

            var dim = g.DimOrName(other); var num = g.Num(main);
            if (dim is null || num is null) return null;

            var op = g.Pick(new[] { "sum", "sum", "count", "avg", "median" });

            var plan = P.Agg(main.Name);
            plan["join"] = P.Arr(other.Name);
            // Çok kaynaklı sorguda kolonlar "VeriSetiAdı.kolon" biçiminde nitelenmeli:
            // aynı ad iki sette birden bulunabilir (ör. "sehir").
            plan["groupBy"] = P.Arr($"{other.Name}.{dim.Name}");
            plan["metrics"] = new JsonArray
            {
                op == "count" ? P.Metric("count") : P.Metric(op, $"{main.Name}.{num.Name}")
            };

            var question = op switch
            {
                "count" => g.Pick(new[]
                {
                    $"{other.Singular} {dim.LabelPoss} bazında {main.Singular} adedi",
                    $"{other.Singular} {dim.LabelPoss} bazında kaç {main.Singular} var"
                }),
                "avg" => g.Pick(new[]
                {
                    $"{other.Singular} {dim.LabelPoss} bazında ortalama {num.Label}",
                    $"{other.Singular} {dim.LabelPoss} kırılımında {num.Label} ortalaması"
                }),
                "median" => $"{other.Singular} {dim.LabelPoss} bazında medyan {num.Label}",
                _ => g.Pick(new[]
                {
                    $"{other.Singular} {dim.LabelPoss} bazında toplam {num.Label}",
                    $"toplam {num.Label} {other.Singular} {dim.LabelPoss} bazında nasıl dağılıyor",
                    $"{main.Genitive} toplam {num.LabelPoss}, {other.Singular} {dim.LabelPoss} kırılımında",
                    $"{other.Singular} {dim.LabelPoss} kırılımında {num.Label} toplamı"
                })
            };

            return new Draft(question, plan);
        }),

        new("join_filter", (g, c) =>
        {
            if (c.Relations.Length == 0) return null;

            var relation = g.Pick(c.Relations);
            var main = c.Datasets.First(d => d.Name == relation.FromDataset);
            var other = c.Datasets.First(d => d.Name == relation.ToDataset);

            var dim = g.DimOrName(other); var num = g.Num(main);
            if (dim is null || num is null || dim.Values.Length == 0) return null;

            var value = g.Val(dim);
            var op = g.Pick(new[] { "sum", "count", "avg" });

            var plan = P.Agg(main.Name);
            plan["join"] = P.Arr(other.Name);
            plan["metrics"] = new JsonArray
            {
                op == "count" ? P.Metric("count") : P.Metric(op, $"{main.Name}.{num.Name}")
            };
            plan.With(P.Leaf($"{other.Name}.{dim.Name}", "eq", value));

            var question = op switch
            {
                "count" => $"{dim.LabelPoss} {value} olan {other.Genitive} kaç {main.Singular} kaydı var",
                "sum" => $"{dim.LabelPoss} {value} olan {other.Genitive} toplam {num.LabelPoss}",
                _ => $"{dim.LabelPoss} {value} olan {other.Genitive} ortalama {num.LabelPoss}"
            };

            return new Draft(question, plan);
        }),

        new("join_rows", (g, c) =>
        {
            if (c.Relations.Length == 0) return null;

            var relation = g.Pick(c.Relations);
            var main = c.Datasets.First(d => d.Name == relation.FromDataset);
            var other = c.Datasets.First(d => d.Name == relation.ToDataset);

            var name = g.NameCol(other); var num = g.Num(main);
            if (name is null || num is null) return null;

            var highest = g.Chance(0.7);
            var n = g.Pick(new[] { 1, 1, 1, 3, 5 });

            var plan = P.Rows(main.Name);
            plan["join"] = P.Arr(other.Name);
            plan["select"] = P.Arr($"{other.Name}.{name.Name}");
            plan["sort"] = $"{main.Name}.{num.Name}";
            plan["dir"] = highest ? "desc" : "asc";
            plan["limit"] = n;

            string question;
            if (n == 1)
                question = highest
                    ? g.Pick(new[]
                    {
                        $"en yüksek {num.Label} hangi {other.Singular} kaydına ait",
                        $"{num.Label} en yüksek olan {main.Singular} hangi {other.Singular} {name.LabelPoss}",
                        $"en çok {num.Label} olan {main.Singular} kaydının {other.Singular} {name.LabelPoss} nedir",
                        $"{main.Genitive} en yükseği hangi {other.Singular} {name.LabelPoss} ile ilişkili",
                        $"en büyük {num.Label} kaydında {other.Singular} {name.LabelPoss} ne"
                    })
                    : g.Pick(new[]
                    {
                        $"en düşük {num.Label} hangi {other.Singular} kaydına ait",
                        $"{num.Label} en düşük olan {main.Singular} kaydının {other.Singular} {name.LabelPoss} nedir",
                        $"{num.LabelPoss} en az olan {main.Singular} hangi {other.Singular} {name.LabelPoss}"
                    });
            else
                question = highest
                    ? g.Pick(new[]
                    {
                        $"{num.Label} en yüksek {n} {main.Singular} kaydının {other.Singular} {name.LabelPoss}",
                        $"en çok {num.Label} olan ilk {n} {main.Singular} hangi {other.Singular} {name.LabelPoss}"
                    })
                    : $"{num.Label} en düşük {n} {main.Singular} kaydının {other.Singular} {name.LabelPoss}";

            return new Draft(question, plan);
        }),

        // ===================== CEVAPLANAMAYAN =====================
        //
        // Bu tarifler en az diğerleri kadar önemli: model her soruya bir plan uydurmaya
        // çalışırsa kullanıcı sessiz yanlış cevap alır. "Cevaplayamıyorum" demeyi de
        // öğrenmesi gerekiyor.

        new("unsupported_future", (g, c) =>
        {
            var ds = g.Ds(c);
            var num = g.Num(ds);
            if (num is null) return null;

            var horizon = g.Pick(new[] { "gelecek ay", "gelecek yıl", "önümüzdeki çeyrek", "seneye" });

            var question = g.Pick(new[]
            {
                $"{horizon} toplam {num.Label} ne olur",
                $"{horizon} ne kadar {num.Label} bekleniyor",
                $"{horizon} {ds.Genitive} {num.LabelPoss} tahmini nedir"
            });

            return new Draft(question, P.Unsupported(
                "Gelecek tahmini yapılamıyor; yalnız kayıtlı veriler sorgulanabilir."));
        }),

        new("unsupported_why", (g, c) =>
        {
            var ds = g.Ds(c);
            var num = g.Num(ds);
            if (num is null) return null;

            var question = g.Pick(new[]
            {
                $"{num.Label} neden düştü",
                $"{ds.Genitive} {num.LabelPoss} neden arttı",
                $"{num.Label} değişiminin sebebi ne",
                $"{num.Label} niçin bu kadar dalgalı",
                $"{ds.Genitive} {num.LabelPoss} neden beklentinin altında",
                $"{num.Label} düşüşünü neye bağlıyorsun",
                $"{ds.Plural} hakkında yorum yap"
            });

            return new Draft(question, P.Unsupported(
                "Neden sorusu cevaplanamıyor; veriler yalnız özetlenebilir, yorumlanamaz."));
        }),

        new("unsupported_absent", (g, c) =>
        {
            var ds = g.Ds(c);
            var absent = g.Pick(new[]
            {
                "hava durumu", "müşteri memnuniyet puanı", "rakip fiyatları",
                "personel doğum tarihi", "stok raf ömrü", "reklam bütçesi"
            });

            var question = g.Pick(new[]
            {
                $"{absent} nedir",
                $"{ds.Genitive} {absent} bilgisi ne durumda",
                $"{absent} ile ilgili özet ver"
            });

            return new Draft(question, P.Unsupported(
                "Bu bilgi mevcut veri setlerinde bulunmuyor."));
        })
    };
}
