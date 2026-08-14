using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Belge çıkarımının KARAR mantığını doğrular: küçültme, bağlam taşması tespiti, ayrıştırma.
//
// Model çağrısı sahte bir istemcinin arkasında — testler Ollama olmadan koşar. Buradaki
// asıl mesele "model doğru okuyor mu" değil (o ölçüm işi, tools/belge_ureteci), sunucunun
// modelin YANILDIĞI durumu fark edip etmediği.
public class DocumentVisionTests
{
    // Çağrıları kaydeden ve önceden yazılmış yanıtları sırayla veren sahte istemci.
    private sealed class FakeVisionClient : IOllamaVisionClient
    {
        private readonly Queue<VisionCall> _responses;

        public FakeVisionClient(params VisionCall[] responses) =>
            _responses = new Queue<VisionCall>(responses);

        public List<(int Bytes, int NumCtx)> Calls { get; } = new();
        public List<string> Prompts { get; } = new();
        public List<int> ImageLongEdges { get; } = new();
        public List<(int Width, int Height)> ImageSizes { get; } = new();

        public Task<VisionCall> GenerateAsync(string prompt, byte[] jpeg, string model,
            int numCtx, CancellationToken ct = default)
        {
            Calls.Add((jpeg.Length, numCtx));
            Prompts.Add(prompt);

            using var image = Image.Load(jpeg);
            ImageLongEdges.Add(Math.Max(image.Width, image.Height));
            ImageSizes.Add((image.Width, image.Height));

            // Yanıt biterse sonuncusu tekrarlanır (deneme sayısı testleri için).
            return Task.FromResult(_responses.Count > 1 ? _responses.Dequeue() : _responses.Peek());
        }
    }

    private static readonly IReadOnlyList<ColumnSchema> Schema = new[]
    {
        new ColumnSchema("fatura_no", "text"),
        new ColumnSchema("tarih", "date"),
        new ColumnSchema("urun", "text"),
        new ColumnSchema("tutar", "number"),
    };

    private const string GecerliYanit = """
        {"alanlar":{"fatura_no":"F-1001","tarih":"2026-08-01"},
         "kalemler":[{"urun":"Kalem","tutar":"1.500,75"},{"urun":"Defter","tutar":"250,00"}]}
        """;

    private static DocumentVisionService Service(IOllamaVisionClient client,
        VisionOptions? options = null) =>
        new(client, Options.Create(options ?? new VisionOptions()),
            NullLogger<DocumentVisionService>.Instance);

    // İstenen boyutta gerçek bir JPEG üretir (sahte baytlar ImageSharp'ı geçemez).
    private static MemoryStream JpegOf(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height);
        var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new JpegEncoder { Quality = 90 });
        stream.Position = 0;
        return stream;
    }

    [Fact(DisplayName = "Belge: sığan görüntüde tek çağrı yapılır ve sonuç şüpheli DEĞİLDİR")]
    public async Task SigarsaTekCagri()
    {
        var client = new FakeVisionClient(new VisionCall(GecerliYanit, PromptTokens: 1800, EvalCount: 120));
        var result = await Service(client).ExtractAsync(JpegOf(1200, 900), Schema);

        Assert.Single(client.Calls);
        Assert.Equal(1, result.Attempts);
        Assert.False(result.Suspect);
        Assert.Empty(result.Warnings);
    }

    [Fact(DisplayName = "Belge: bağlam taşarsa görüntü küçültülüp YENİDEN denenir")]
    public async Task TasarsaKucultupTekrarDener()
    {
        // İlk çağrı sınıra dayanıyor (4096 - 512 = 3584 eşiği), ikincisi sığıyor.
        var client = new FakeVisionClient(
            new VisionCall(GecerliYanit, PromptTokens: 3900, EvalCount: 0),
            new VisionCall(GecerliYanit, PromptTokens: 1500, EvalCount: 120));

        var result = await Service(client).ExtractAsync(JpegOf(2400, 1800), Schema);

        Assert.Equal(2, client.Calls.Count);
        Assert.Equal(2, result.Attempts);
        Assert.False(result.Suspect);

        // İkinci gönderim gerçekten daha küçük olmalı — yoksa "yeniden deneme" bir şey
        // değiştirmiyor demektir.
        Assert.True(client.ImageLongEdges[1] < client.ImageLongEdges[0],
            $"ikinci gönderim küçülmedi: {client.ImageLongEdges[0]} → {client.ImageLongEdges[1]}");
    }

    [Fact(DisplayName = "Belge: küçültmeye rağmen sığmazsa sonuç ŞÜPHELİ işaretlenir (sessiz kalmaz)")]
    public async Task SigmazsaSupheliIsaretlenir()
    {
        // Her denemede taşıyor.
        var client = new FakeVisionClient(new VisionCall(GecerliYanit, PromptTokens: 4000, EvalCount: 0));

        var result = await Service(client).ExtractAsync(JpegOf(4000, 3000), Schema);

        Assert.True(result.Suspect);
        Assert.Equal(3, result.Attempts);           // MaxAttempts
        Assert.Contains(result.Warnings, w => w.Contains("sığmadı"));

        // Sonuç yine de dönüyor: yarısı okunmuş belge, hiç okunmamıştan iyidir.
        Assert.NotEmpty(result.Table.Rows);
    }

    [Fact(DisplayName = "Belge: en küçük boya inince daha fazla denenmez")]
    public async Task MinimumBoydaDurur()
    {
        var client = new FakeVisionClient(new VisionCall(GecerliYanit, PromptTokens: 4000, EvalCount: 0));
        var options = new VisionOptions { MaxLongEdge = 800, MinLongEdge = 700, ShrinkFactor = 0.5 };

        var result = await Service(client, options).ExtractAsync(JpegOf(1600, 1200), Schema);

        // 800 * 0.5 = 400 < MinLongEdge → ikinci deneme yapılmaz.
        Assert.Single(client.Calls);
        Assert.True(result.Suspect);
    }

    [Fact(DisplayName = "Belge: ayrıştırılamayan yanıt boş sonuç değil HATA üretir")]
    public async Task AyristirilamayanYanitHataVerir()
    {
        var client = new FakeVisionClient(new VisionCall("kusura bakma, okuyamadım", 500, 20));

        var ex = await Assert.ThrowsAsync<InvalidQueryException>(
            () => Service(client).ExtractAsync(JpegOf(800, 600), Schema));

        Assert.Contains("okunamadı", ex.Message);
    }

    [Fact(DisplayName = "Belge: şemasız veri setinde çıkarım denenmez")]
    public async Task SemasizSetReddedilir()
    {
        var client = new FakeVisionClient(new VisionCall(GecerliYanit, 500, 20));

        await Assert.ThrowsAsync<InvalidQueryException>(
            () => Service(client).ExtractAsync(JpegOf(800, 600), Array.Empty<ColumnSchema>()));

        Assert.Empty(client.Calls);   // model hiç çağrılmadı
    }

    [Fact(DisplayName = "Belge: büyük görüntü İLK gönderimde bütçeye göre küçültülür")]
    public async Task BuyukGorselOncedenKucultulur()
    {
        var client = new FakeVisionClient(new VisionCall(GecerliYanit, 1500, 120));

        await Service(client).ExtractAsync(JpegOf(4000, 3000), Schema);

        // Tahmin doğru olmasa bile üst sınır uygulanmalı: ham 4000 px gönderilmemeli.
        Assert.True(client.ImageLongEdges[0] <= 1800,
            $"ilk gönderim küçültülmedi: {client.ImageLongEdges[0]} px");
    }

    [Fact(DisplayName = "Belge: küçük görüntü BÜYÜTÜLMEZ")]
    public async Task KucukGorselBuyutulmez()
    {
        var client = new FakeVisionClient(new VisionCall(GecerliYanit, 900, 120));

        await Service(client).ExtractAsync(JpegOf(640, 480), Schema);

        Assert.Equal(640, client.ImageLongEdges[0]);
    }

    [Theory(DisplayName = "Belge: belirteç bütçesi uzun kenara çevrilirken en-boy oranı korunur")]
    [InlineData(2000, 1000, 3000)]
    [InlineData(1000, 2000, 3000)]
    [InlineData(1600, 1200, 1000)]
    public void FitLongEdgeOraniKorur(int width, int height, int budget)
    {
        var longEdge = DocumentVisionService.FitLongEdge(width, height, budget);

        Assert.True(longEdge > 0);
        Assert.True(longEdge <= Math.Max(width, height), "büyütme yapılmamalı");

        // Hesaplanan boyun tahmini belirteç sayısı bütçeyi aşmamalı (yuvarlama payıyla).
        var ratio = (double)Math.Min(width, height) / Math.Max(width, height);
        var tokens = longEdge * (longEdge * ratio) / (28 * 28);
        Assert.True(tokens <= budget * 1.05, $"tahmini {tokens:F0} belirteç, bütçe {budget}");
    }

    [Fact(DisplayName = "Belge: kalemleri belge alanlarının tekrarı olan satır düşürülür ve NOT bırakılır")]
    public async Task BilgiTasimayanKalemDusurulur()
    {
        const string tekrarEdenKalem = """
            {"alanlar":{"aciklama":"Danışmanlık","tutar":"5000.00"},
             "kalemler":[{"aciklama":"Danışmanlık","tutar":"5000.00"}]}
            """;

        var client = new FakeVisionClient(new VisionCall(tekrarEdenKalem, 1200, 90));
        var result = await Service(client).ExtractAsync(JpegOf(900, 700), Schema);

        Assert.Empty(result.Document.Items);
        Assert.Contains(result.Warnings, w => w.Contains("Tek kalem satırı düşürüldü"));
    }

    [Fact(DisplayName = "İstem: hedef şemanın kolonları ve Türkçe tip adları yazılır")]
    public void IstemSemayiIcerir()
    {
        var prompt = DocumentPromptBuilder.Build(Schema);

        Assert.Contains("fatura_no (metin)", prompt);
        Assert.Contains("tarih (tarih)", prompt);
        Assert.Contains("tutar (sayı)", prompt);
        Assert.Contains("başka ad uydurma", prompt);
        Assert.Contains("Kalem UYDURMA", prompt);
    }

    // ---- keşif geçişi: hedef şema YOK ----

    private const string KesifYaniti = """
        {"belge_turu":"fatura",
         "alanlar":{"fatura_no":"F-1001","fatura_tarihi":"2026-08-01"},
         "kalemler":[{"urun_adi":"Kalem","tutar":"1500.75"},{"urun_adi":"Defter","tutar":"250.00"}]}
        """;

    [Fact(DisplayName = "Keşif: şema verilmeden okunur, kolon adları modelden gelir")]
    public async Task KesifSemasizCalisir()
    {
        var client = new FakeVisionClient(new VisionCall(KesifYaniti, 1700, 140));

        var result = await Service(client).DiscoverAsync(JpegOf(1200, 900));

        // Şemalı geçişte olmayan tek şart: burada set seçilmediği için istem de şema
        // listesi taşımaz.
        Assert.DoesNotContain("Alanlar (tam bu adlarla", client.Prompts[0]);
        Assert.Equal(new[] { "fatura_no", "fatura_tarihi", "urun_adi", "tutar" },
            result.Table.Headers);
        Assert.Equal(2, result.Table.Rows.Count);
    }

    [Fact(DisplayName = "Keşif: belge türü okunur ama KOLON yapılmaz (her satırda aynı değer veri değildir)")]
    public async Task BelgeTuruKolonOlmaz()
    {
        var client = new FakeVisionClient(new VisionCall(KesifYaniti, 1700, 140));

        var result = await Service(client).DiscoverAsync(JpegOf(1200, 900));

        Assert.Equal("fatura", result.Document.DocumentType);
        Assert.DoesNotContain("belge_turu", result.Table.Headers);
    }

    [Fact(DisplayName = "Keşif: belge türü alanların içine düşse de kolon yapılmaz")]
    public void BelgeTuruAlanlarinIcindeOlabilir()
    {
        // Gözlenen davranış: model sözleşmedeki yeri tutturamayıp değeri `alanlar`a koyuyor
        // (kalem dizisinde de aynısı oluyordu). Katı ayrıştırıcı burada kolon uydururdu.
        const string yanit = """
            {"alanlar":{"belge_turu":"fis","tutar":"120,50"},"kalemler":[]}
            """;

        var belge = DocumentExtractionParser.Parse(yanit)!;

        Assert.Equal("fis", belge.DocumentType);
        Assert.DoesNotContain("belge_turu", belge.Fields.Keys);
        Assert.Equal("120.50", belge.Fields["tutar"]);
    }

    [Fact(DisplayName = "Keşif: taşma tespiti ve küçültme şemalı geçişle AYNI yoldan geçer")]
    public async Task KesifDeTasmaTespitiCalisir()
    {
        var client = new FakeVisionClient(
            new VisionCall(KesifYaniti, PromptTokens: 3900, EvalCount: 0),
            new VisionCall(KesifYaniti, PromptTokens: 1500, EvalCount: 140));

        var result = await Service(client).DiscoverAsync(JpegOf(2400, 1800));

        Assert.Equal(2, result.Attempts);
        Assert.False(result.Suspect);
        Assert.True(client.ImageLongEdges[1] < client.ImageLongEdges[0]);
    }

    // ---- başlık şeridi (ölçümde %51 → %94) ----

    // Görüntünün oranı: şerit eklenince yükseklik artar, genişlik değişmez.
    private static double Oran((int Width, int Height) size) => (double)size.Height / size.Width;

    [Fact(DisplayName = "Şerit: AÇIKKEN şemalı geçişte görüntünün üstüne eklenir")]
    public async Task SemaliGecisteSeritEklenir()
    {
        var seritli = new FakeVisionClient(new VisionCall(GecerliYanit, 1500, 120));
        var seritsiz = new FakeVisionClient(new VisionCall(GecerliYanit, 1500, 120));

        await Service(seritli, new VisionOptions { HeaderBand = true })
            .ExtractAsync(JpegOf(1200, 900), Schema);
        await Service(seritsiz).ExtractAsync(JpegOf(1200, 900), Schema);

        // Şerit yalnız yükseklik ekler: aynı belge daha uzun bir görüntü olarak gider.
        Assert.True(Oran(seritli.ImageSizes[0]) > Oran(seritsiz.ImageSizes[0]),
            $"şerit eklenmemiş görünüyor: {seritli.ImageSizes[0]} vs {seritsiz.ImageSizes[0]}");
    }

    [Fact(DisplayName = "Şerit: AÇIK olsa bile KEŞİF geçişinde eklenmez (şerit hedef şemadan üretilir)")]
    public async Task KesifGecisindeSeritEklenmez()
    {
        var acik = new VisionOptions { HeaderBand = true };
        var kesif = new FakeVisionClient(new VisionCall(KesifYaniti, 1500, 120));
        var semali = new FakeVisionClient(new VisionCall(GecerliYanit, 1500, 120));

        await Service(kesif, acik).DiscoverAsync(JpegOf(1200, 900));
        await Service(semali, acik).ExtractAsync(JpegOf(1200, 900), Schema);

        Assert.True(Oran(kesif.ImageSizes[0]) < Oran(semali.ImageSizes[0]),
            "keşif geçişine de şerit eklenmiş");
    }

    [Fact(DisplayName = "Şerit: VARSAYILAN KAPALI — faydası üretim yolunda üretilemedi (bkz. VisionOptions)")]
    public async Task VarsayilanKapali()
    {
        var client = new FakeVisionClient(new VisionCall(GecerliYanit, 1500, 120));

        await Service(client).ExtractAsync(JpegOf(1200, 900), Schema);

        // Şerit eklenseydi yükseklik artar, oran büyürdü.
        Assert.Equal(900.0 / 1200, Oran(client.ImageSizes[0]), 3);
    }

    [Fact(DisplayName = "Şerit: kolon adlarından üretilir ve orijinal görüntüyü BOZMAZ")]
    public void SeritOrijinaliBozmaz()
    {
        using var kaynak = new Image<Rgb24>(1000, 700);

        var eklendi = DocumentImagePrep.TryAddHeaderBand(kaynak,
            Schema.Select(c => c.Name).ToList(), out var seritli);

        Assert.True(eklendi, "yazı tipi bulunamadı — bu ortamda şerit çizilemiyor");
        using (seritli)
        {
            Assert.Equal(kaynak.Width, seritli!.Width);         // genişlik korunur
            Assert.True(seritli.Height > kaynak.Height);        // yalnız üste eklenir
            Assert.True(seritli.Height - kaynak.Height < kaynak.Height / 4,
                "şerit belgenin kendisini gölgeleyecek kadar yüksek");
        }
    }

    [Fact(DisplayName = "Şerit: kolon yoksa çizilmez (boş şerit belgeyi büyütmekten başka işe yaramaz)")]
    public void KolonsuzSeritCizilmez()
    {
        using var kaynak = new Image<Rgb24>(800, 600);

        Assert.False(DocumentImagePrep.TryAddHeaderBand(kaynak, Array.Empty<string>(), out var yok));
        Assert.Null(yok);
    }

    [Fact(DisplayName = "İstem: keşif istemi adlandırma kurallarını ve belge türünü ister")]
    public void KesifIstemiKurallariIcerir()
    {
        var prompt = DocumentPromptBuilder.BuildDiscovery();

        Assert.Contains("hazır bir alan listesi verilmedi", prompt);
        Assert.Contains("belge_turu", prompt);
        Assert.Contains("küçük harf ve alt çizgili", prompt);
        Assert.Contains("UYDURMA", prompt);

        // Şerit keşifte eklenemiyor (şemadan üretiliyor); ilk satır kaybına karşı elde
        // yalnız bu madde var, düşmemeli.
        Assert.Contains("EN ÜSTTEKİ satır da", prompt);
    }
}
