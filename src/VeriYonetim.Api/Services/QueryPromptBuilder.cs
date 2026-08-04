using System.Text;

namespace VeriYonetim.Api.Services;

// Modele gönderilen istemi kurar.
//
// İstemin kalitesi bu adımın kalitesidir: 7B'lik bir model firmanın veri setlerini ezberden
// bilmez, dönem etiketlerini tahmin edemez. Bu yüzden bütün katalog ve geçerli etiketler
// istemin İÇİNE yazılır — model uydurmak yerine verilen listeden seçer.
//
// Örnekler (few-shot) bilinçli olarak çeşitli: küçük modeller tek desen gördüklerinde
// her soruyu ona benzetme eğilimindedir.
public static class QueryPromptBuilder
{
    public static string Build(string question, TenantCatalog catalog)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Sen bir veri sorgulama asistanısın. Kullanıcının Türkçe sorusunu JSON sorgu planına çevirirsin.");
        sb.AppendLine("SADECE JSON döndür. Açıklama, yorum satırı veya kod bloğu işareti yazma.");
        sb.AppendLine();

        AppendCatalog(sb, catalog);
        AppendSchema(sb);
        AppendValues(sb);
        AppendRules(sb);

        sb.AppendLine("## Örnekler");
        sb.AppendLine(Examples);
        sb.AppendLine();

        sb.AppendLine("## Soru");
        sb.AppendLine(question);

        return sb.ToString();
    }

    private static void AppendCatalog(StringBuilder sb, TenantCatalog catalog)
    {
        sb.AppendLine("## Veri setleri");

        foreach (var dataset in catalog.Datasets)
        {
            sb.Append($"- {dataset.Name}");
            if (!string.IsNullOrWhiteSpace(dataset.Description))
                sb.Append($" — {dataset.Description}");
            sb.AppendLine();

            foreach (var column in dataset.Columns)
                sb.AppendLine($"    {column.Key} ({TypeLabel(column.Value)})");
        }

        sb.AppendLine();

        // İlişkiler yalnız tanımlıysa yazılır. Boş bir "İlişkiler" başlığı modeli JOIN
        // üretmeye özendirebilir; olmayan bağ üzerinden kurulan sorgu ise hata verir.
        if (catalog.Relations.Count > 0)
        {
            sb.AppendLine("## Tanımlı ilişkiler (yalnız bunlar üzerinden birleştirebilirsin)");

            foreach (var relation in catalog.Relations)
            {
                var from = catalog.Datasets.FirstOrDefault(d => d.Id == relation.FromDatasetId);
                var to = catalog.Datasets.FirstOrDefault(d => d.Id == relation.ToDatasetId);
                if (from is null || to is null) continue;

                sb.AppendLine($"- {from.Name}.{relation.FromColumn} = {to.Name}.{relation.ToColumn}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendSchema(StringBuilder sb)
    {
        sb.AppendLine("## Plan şeması");
        sb.AppendLine("""
            {
              "kind": "rows" | "aggregate" | "unsupported",
              "reason": "yalnız kind=unsupported ise: neden cevaplanamadığı",

              "from": "<veri seti adı>",
              "join": ["<birleştirilecek veri seti adı>"],

              "filters": [
                {"column":"<kolon>", "op":"<operatör>", "value":"<değer>"},
                {"column":"<kolon>", "op":"in", "values":["<değer>","<değer>"]},
                {"logic":"or", "children":[ {...}, {...} ]}
              ],

              "sort": "<kolon adı (kind=rows) | 'key' | 'value'>",
              "dir": "asc" | "desc",
              "limit": <sayı>,

              "groupBy": ["<kolon>"],
              "bucket": "day" | "week" | "month" | "year",
              "metrics": [{"op":"<işlem>", "column":"<kolon>"}],
              "having": {"metric":<sıra>, "op":"gt", "value":<sayı>},
              "share": true,
              "sortMetric": <sıra>,
              "compare": {"column":"<tarih kolonu>", "period":"<dönem>", "previous":"<dönem>"}
            }
            """);
        sb.AppendLine();
    }

    private static void AppendValues(StringBuilder sb)
    {
        sb.AppendLine("## Değerler");
        sb.AppendLine("- işlem (metrics.op): count, sum, avg, min, max, countDistinct");
        sb.AppendLine("  sum/avg/min/max yalnız SAYI kolonlarında; countDistinct her tipte; count kolon istemez.");
        sb.AppendLine("- operatör (filters.op): eq, ne, gt, gte, lt, lte, contains, in, notIn, isNull, notNull, inPeriod");
        sb.AppendLine("  contains yalnız METİN kolonlarında; inPeriod yalnız TARİH kolonlarında.");
        sb.AppendLine($"- dönem (inPeriod ve compare): {string.Join(", ", RelativePeriod.Tokens)}");
        sb.AppendLine();
    }

    private static void AppendRules(StringBuilder sb)
    {
        sb.AppendLine("## Kurallar");
        sb.AppendLine("- from ZORUNLU: sorunun hangi veri setinden başladığını yaz.");
        sb.AppendLine("- Sorunun cevabı için başka bir setteki kolon gerekiyorsa o seti join'e ekle.");
        sb.AppendLine("  join YALNIZCA yukarıda tanımlı ilişki varsa kullanılabilir. İlişki yoksa kind=unsupported.");
        sb.AppendLine("- Birden çok set kullanıyorsan kolonları \"VeriSetiAdı.kolon\" biçiminde yaz.");
        sb.AppendLine("  Tek set kullanıyorsan sade kolon adı yeter.");
        sb.AppendLine("- Soru bir LİSTE istiyorsa (\"hangileri\", \"göster\", \"listele\") kind=rows kullan.");
        sb.AppendLine("- Soru bir HESAP istiyorsa (\"toplam\", \"ortalama\", \"kaç tane\") kind=aggregate kullan.");
        sb.AppendLine("- Tarihleri kendin hesaplama. \"bu yıl\" için inPeriod + dönem etiketi kullan.");
        sb.AppendLine("- \"A ve B'deki\" gibi çoklu değerde ayrı ayrı eq DEĞİL, tek bir in kullan.");
        sb.AppendLine("- Soruyu bu şemayla ifade edemiyorsan kind=unsupported döndür ve reason yaz.");
        sb.AppendLine("  Uydurma. Yanlış cevap vermektense cevaplayamadığını söylemen daha iyidir.");
        sb.AppendLine("- Kullanmadığın alanları hiç yazma.");
        sb.AppendLine();
    }

    private static string TypeLabel(string type) => type switch
    {
        "number" => "sayı",
        "date" => "tarih",
        _ => "metin"
    };

    // Örneklerdeki veri seti ve kolon adları kasıtlı olarak GENEL. Gerçek katalog istemin
    // başında verildiği için model eşlemeyi kendisi yapar; örneklerde gerçek adları
    // kullansaydık model onları körü körüne kopyalamaya meyleder.
    private const string Examples = """
        Soru: Şehirlere göre toplam satış
        {"kind":"aggregate","from":"Satislar","groupBy":["sehir"],"metrics":[{"op":"sum","column":"tutar"}]}

        Soru: En pahalı 5 ürün hangileri
        {"kind":"rows","from":"Satislar","sort":"tutar","dir":"desc","limit":5}

        Soru: Ankara ve İzmir'deki satışların adedi
        {"kind":"aggregate","from":"Satislar","metrics":[{"op":"count"}],"filters":[{"column":"sehir","op":"in","values":["Ankara","İzmir"]}]}

        Soru: Bu yıl aylara göre ciro
        {"kind":"aggregate","from":"Satislar","groupBy":["tarih"],"bucket":"month","metrics":[{"op":"sum","column":"tutar"}],"filters":[{"column":"tarih","op":"inPeriod","value":"buYil"}]}

        Soru: Ortalama satışı 1000'in üstünde olan şehirler
        {"kind":"aggregate","from":"Satislar","groupBy":["sehir"],"metrics":[{"op":"avg","column":"tutar"}],"having":{"metric":0,"op":"gt","value":1000}}

        Soru: Kaç farklı şehre satış yaptık
        {"kind":"aggregate","from":"Satislar","metrics":[{"op":"countDistinct","column":"sehir"}]}

        Soru: Her şehrin toplam ciro içindeki yüzdesi
        {"kind":"aggregate","from":"Satislar","groupBy":["sehir"],"metrics":[{"op":"sum","column":"tutar"}],"share":true}

        Soru: Satışları müşterinin şehrine göre grupla
        (Satislar.musteri_no = Musteriler.no ilişkisi tanımlıysa)
        {"kind":"aggregate","from":"Satislar","join":["Musteriler"],"groupBy":["Musteriler.sehir"],"metrics":[{"op":"sum","column":"Satislar.tutar"}]}

        Soru: Bu ay geçen aya göre şehir bazında nasıl
        {"kind":"aggregate","from":"Satislar","groupBy":["sehir"],"metrics":[{"op":"sum","column":"tutar"}],"compare":{"column":"tarih","period":"buAy","previous":"gecenAy"}}

        Soru: Gelecek ay ne kadar satarız
        {"kind":"unsupported","reason":"Gelecek tahmini yapılamıyor; yalnız kayıtlı veriler sorgulanabilir."}
        """;
}
