using System.Security.Claims;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Dtos;
using VeriYonetim.Api.Models.Entities;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Controllers;

/// <summary>
/// Belge (fatura, fiş, makbuz) yükleme — veri girişinin ÜÇÜNCÜ kapısı.
///
/// Ana karar: belgeden çıkan şey yapılandırılmış bir tablodur; oradan sonrası zaten var
/// olan yoldur (tip algılama → şema → JSONB satır). Ayrı bir "Fatura" tablosu AÇILMIYOR:
/// yüklenen belge her zaman fatura olmayabilir, üstelik ikinci bir veri modeli panoyu ve
/// doğal dilde sorguyu ayrıca bağlamayı gerektirirdi.
///
/// Sınıf düzeyindeki politika DatasetsController ile aynı — platform yöneticisi buraya da
/// giremez. Yazma yetkisi Editor/Admin: belge yüklemek veri girişidir.
/// </summary>
/// Yol şablonu eylem başına yazılıyor çünkü iki uç iki farklı düzeyde duruyor: çıkarım bir
/// veri setinin altında (hedef bellidir), keşif firma düzeyinde (hedef henüz yoktur).
[ApiController]
[Route("api")]
[Authorize(Policy = AuthPolicies.TenantUser)]
public class DocumentsController : ControllerBase
{
    // Belge görüntüleri fotoğraf olduğu için CSV'den büyük olabilir.
    private const long MaxUploadBytes = 15 * 1024 * 1024; // 15 MB

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    // Tek belgeden kaydedilebilecek en fazla satır. Bir fatura onlarca kalem taşır, yüzlerce
    // değil; bu sınır kötü niyetli ya da bozuk bir isteğin veritabanını şişirmesini engeller.
    private const int MaxConfirmRows = 500;

    // Tek belgeyle sete eklenebilecek en fazla yeni kolon. Belge şemayı GENİŞLETEBİLİR ama
    // yeniden tanımlayamaz: on beş kolonluk bir ek, eşlemenin yanlış gittiğinin işaretidir.
    private const int MaxNewColumns = 15;

    /// Veritabanındaki `DatasetColumn.Name` sınırıyla AYNI (varchar(200)). Burada
    /// denetlenmezse ihlal SaveChanges'te, sebebi görünmeyen bir 500 olarak çıkıyor.
    private const int MaxColumnNameLength = 200;

    // Saklanan görüntünün uzun kenarı. Modele giden boydan bağımsız: bu değer insan gözüne
    // göre seçildi (fatura kalem satırları okunabilsin), o ise bağlam bütçesine göre.
    private const int StoredImageLongEdge = 2000;

    private readonly AppDbContext _db;
    private readonly IDatasetImportService _importService;
    private readonly IBackgroundJobClient _jobs;
    private readonly ITenantContext _tenantContext;
    private readonly IRelationDetector _relationDetector;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(AppDbContext db, IDatasetImportService importService,
        IBackgroundJobClient jobs, ITenantContext tenantContext,
        IRelationDetector relationDetector, ILogger<DocumentsController> logger)
    {
        _db = db;
        _importService = importService;
        _jobs = jobs;
        _tenantContext = tenantContext;
        _relationDetector = relationDetector;
        _logger = logger;
    }

    /// <summary>
    /// Belgeyi okuma işini KUYRUĞA ALIR ve hemen döner — sonucu beklemez.
    ///
    /// Neden senkron değil: belge okuma 30-150 saniye sürüyor, çok sayfalıda daha da
    /// uzuyor. Bu süreyi HTTP isteğinin içinde geçirmek üç yerde kırılıyordu — vekil
    /// sunucu bağlantıyı kesiyor, kullanıcı sekmeyi kapatınca iş kayboluyor, ekran o
    /// süre boyunca kilitli kalıyor. Uç artık işi kaydedip 202 dönüyor; sonuç hazır
    /// olunca kullanıcıya canlı kanaldan haber gidiyor (bkz. JobsHub).
    /// </summary>
    [HttpPost("datasets/{datasetId:guid}/document/extract")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Extract(Guid datasetId, IFormFile? file,
        CancellationToken ct)
    {
        // Sahiplik doğrulaması: global query filter sayesinde başka firmanın seti burada
        // zaten bulunamaz. 403 değil 404 dönüyoruz (kaydın varlığını sızdırmamak için).
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == datasetId, ct);
        if (dataset is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Veri seti bulunamadı.");

        if (ValidateUpload(file) is { } error) return error;

        // Hedef şema ZORUNLU: model neyi arayacağını bilmeden serbest çıkarım yapar ve alan
        // adlarını uydurur (bkz. DocumentPromptBuilder). Denetim BURADA yapılıyor, işin
        // içinde değil: kullanıcı iki dakika bekleyip "şema yok" hatası almamalı.
        var hasSchema = await _db.DatasetColumns.AnyAsync(c => c.DatasetId == datasetId, ct);
        if (!hasSchema)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu veri seti için önce şema tanımlayın (POST /api/datasets/{id}/schema).");

        return await QueueAsync(DocumentJobKind.Extract, datasetId, file!, ct);
    }

    /// <summary>
    /// Onay ekranındaki tabloyu veri setine EKLER — belgeden veri girişinin son adımı.
    ///
    /// İçe aktarmadan (`POST /rows`) farkı bilinçli: o "değiştir" semantiğiyle çalışır,
    /// çünkü bir dosya setin tamamıdır. Belge ise bir KAYITTIR; her fatura eskilerin
    /// üstüne yazsaydı ikinci belge birinciyi silerdi.
    ///
    /// Ya hep ya hiç: tek bir hücre bile şemaya uymuyorsa hiçbir satır yazılmaz ve hangi
    /// hücrenin uymadığı geri döner. Yarısı yazılmış bir belge, kullanıcının hangi satırın
    /// içeride olduğunu bilemediği bir durum bırakırdı.
    /// </summary>
    [HttpPost("datasets/{datasetId:guid}/document/confirm")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Confirm(Guid datasetId, DocumentConfirmRequest request,
        CancellationToken ct)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == datasetId, ct);
        if (dataset is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Veri seti bulunamadı.");

        var columns = request.Columns ?? Array.Empty<string>();
        var rows = request.Rows ?? Array.Empty<string[]>();

        if (columns.Count == 0 || rows.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Kaydedilecek satır yok.");

        if (rows.Count > MaxConfirmRows)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Tek belgeden en fazla {MaxConfirmRows} satır kaydedilebilir.");

        // Satır uzunluğu başlık sayısıyla uyuşmuyorsa hücreler kayar ve veri SESSİZCE
        // yanlış kolona yazılır — bu yüzden kırpmak/tamamlamak yerine reddediyoruz.
        if (rows.Any(r => r.Length != columns.Count))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Satırlardaki hücre sayısı kolon sayısıyla uyuşmuyor.");

        // Aynı ad iki kolonda: ikisinden biri diğerinin üstüne yazılır ve hangisinin
        // kaydedildiği rastlantıya kalır. Onay ekranında iki belge kolonunu aynı set
        // kolonuna eşlemek buraya böyle düşüyor.
        var duplicate = columns
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"\"{duplicate.Key}\" kolonu birden fazla kez gönderildi; her kolon "
                       + "yalnız bir kez eşlenebilir.");

        var schema = await _db.DatasetColumns
            .Where(c => c.DatasetId == datasetId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnSchema(c.Name, c.Type))
            .ToListAsync(ct);

        if (schema.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu veri seti için önce şema tanımlayın (POST /api/datasets/{id}/schema).");

        // HÜCRELER NORMALLEŞTİRİLİYOR — kod incelemesinde bulunan sessiz bozulmanın
        // kapanışı.
        //
        // `Normalize` bugüne kadar yalnız MODELİN çıktısına uygulanıyordu (ayrıştırma
        // anında). Onay ekranı ise hücreleri düzenlenebilir yapıyor ve kullanıcının
        // yazdığı metin buraya ham geliyordu. Kolonun tip kararı bütün değerlere birden
        // bakılarak verildiği için tek bir Türkçe yazım bütün kolonu tr-TR'ye çeviriyordu:
        //
        //   model "1993.32" ve "721.83" üretti (ikisi de doğru), kullanıcı üçüncü satırı
        //   "1.234,56" diye düzeltti → kolonda virgül belirdi → kültür Türkçe seçildi →
        //   dokunulmayan iki satır 199332 ve 72183 olarak yazıldı. Yüz kat sapma, hatasız.
        //
        // Bu tehlike DocumentExtraction'ın kendi yorumunda birebir tarif edilmişti; eksik
        // olan, korumanın İKİNCİ giriş yoluna — kullanıcının klavyesine — uygulanmasıydı.
        var table = new ParsedTable(columns, NormalizeCells(rows));

        // Kullanıcının EKLENMESİNİ istediği kolonlar şemaya burada katılıyor; tipleri
        // değerlerden algılanıyor (dosyadan içe aktarmayla aynı katman, adlar modelden
        // tipler veriden ilkesinin devamı).
        if (BuildNewColumns(request.NewColumns, table, schema, out var added, out var columnError))
            schema.AddRange(added);
        else
            return columnError!;

        // Şemada karşılığı olmayan kolon SESSİZCE ATILMAZ.
        //
        // Bu denetim bu adımın asıl sebebi. ValidateRows şemadan yürüyor: tabloda olup
        // şemada olmayan kolonu hiç görmüyor, yani belge okundu, ekranda göründü, "kaydedildi"
        // dendi ama o kolon hiç yazılmadı. Ölçülen bir örnekte ürün adları böyle kayboldu ve
        // kullanıcı tek bir uyarı bile almadı. Artık istek reddediliyor: kolon ya bir set
        // kolonuna eşlenecek, ya yeni kolon olarak eklenecek, ya da kullanıcı onu bilerek
        // atacak (o zaman istemci kolonu hiç göndermiyor).
        var schemaNames = schema.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = columns.Where(c => !schemaNames.Contains(c)).ToList();

        if (unknown.Count > 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu kolonların veri setinde karşılığı yok; eşleyin ya da yeni kolon "
                       + "olarak ekleyin.",
                detail: string.Join(", ", unknown));

        // Aynı belge iki kez kaydedilemez.
        //
        // Denetim SUNUCUDA: arayüz onaylanmış işte düğmeyi kapatıyor, ama iş listesi
        // kalıcı olduğu için kullanıcı eski bir işi açıp tekrar gönderebilir — o zaman
        // aynı satırlar sete ikinci kez eklenir ve bu sessiz bir mükerrer kayıt olur.
        // Ekran doğruluğun bekçisi olamaz.
        // İş kimliği ZORUNLU. Eskiden opsiyoneldi ve denetim `if (request.JobId is ...)`
        // içinde duruyordu: alan gönderilmediğinde mükerrer koruması tamamen atlanıyordu.
        // Yani "ekran doğruluğun bekçisi olamaz" diyen denetimin TETİKLENMESİ ekranın
        // doğru alanı göndermesine bağlıydı. Artık gönderilmezse istek reddediliyor.
        if (request.JobId is not { } requestedJobId)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Kaydetmek için belge işi kimliği (jobId) gerekli.");

        // İş kaydı KULLANICIYA da daraltılıyor.
        //
        // Global query filter yalnız firmayı daraltıyordu; oysa belge işlerinin görünürlüğü
        // bilinçli olarak KİŞİSEL (DocumentJobsController'ın dört ucu da UserId denetliyor
        // ve gerekçesi orada yazılı). Bu uç o kuralın dışında kalmıştı ve bedeli ağırdı:
        // aynı firmadaki B kullanıcısı A'nın jobId'siyle kaydettiğinde kendi satırları
        // kendi setine yazılıyor, ama A'nın işine `Image = null` + `ConfirmedAt` damgası
        // basılıyordu. A'nın belgesi geri getirilemez biçimde siliniyor, A ekranda "zaten
        // kaydedilmiş" görüyordu — oysa onun belgesinden hiçbir satır yazılmamıştı.
        //
        // Ayrıca işin BU veri setine ait olduğu ve gerçekten tamamlandığı da denetleniyor;
        // ikisi de eksikti.
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var currentUserId))
            return Problem(statusCode: StatusCodes.Status401Unauthorized,
                title: "Kullanıcı kimliği bulunamadı.");

        var job = await _db.DocumentJobs
            .FirstOrDefaultAsync(j => j.Id == requestedJobId && j.UserId == currentUserId, ct);

        // Başkasının işi 403 değil 404 döner — kaydın varlığı sızdırılmıyor (kardeş ucun
        // kuralıyla aynı).
        if (job is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Belge işi bulunamadı.");

        if (job.ConfirmedAt is not null)
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Bu belge zaten kaydedilmiş; satırlar ikinci kez eklenmez.");

        if (job.DatasetId is { } jobDatasetId && jobDatasetId != datasetId)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu belge işi başka bir veri seti için oluşturulmuş.");

        // Doğrulama içe aktarmayla BİREBİR aynı katmandan geçiyor: belgeden gelen satır ile
        // CSV'den gelen satır aynı kurallara tabi, yoksa iki kapı iki farklı veri üretirdi.
        var validation = _importService.ValidateRows(table, schema);

        if (validation.Errors.Count > 0)
            return ValidationProblem(new ValidationProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bazı hücreler şemaya uymuyor; düzeltip tekrar deneyin.",
                Extensions = { ["cells"] = validation.Errors }
            });

        // Yeni kolonlar satırlarla AYNI işlemde yazılıyor: biri yazılıp diğeri yazılmasaydı
        // sette hiçbir satırda karşılığı olmayan bir kolon ya da tersi kalırdı.
        if (added.Count > 0)
        {
            var ordinal = (await _db.DatasetColumns
                .Where(c => c.DatasetId == datasetId)
                .MaxAsync(c => (int?)c.Ordinal, ct) ?? -1) + 1;

            foreach (var column in added)
                _db.DatasetColumns.Add(new DatasetColumn
                {
                    Id = Guid.NewGuid(),
                    DatasetId = datasetId,
                    Name = column.Name,
                    Type = column.Type,
                    Ordinal = ordinal++
                });
        }

        foreach (var values in validation.ValidRows)
            _db.DatasetRows.Add(new DatasetRow
            {
                Id = Guid.NewGuid(),
                DatasetId = datasetId,
                Data = values
            });

        dataset.RowCount += validation.ValidRows.Count;
        dataset.UpdatedAt = DateTime.UtcNow;

        // Onaylanan belge artık gerekmiyor: görüntü işin ömrüne bağlı bir ara üründü,
        // kalıcı olan az önce yazılan satırlar. Onay damgası ile aynı işlemde yazılıyor —
        // ikisi ayrılırsa "kaydedildi ama işaretlenmedi" hâli mükerrer kayda kapı açardı.
        job.Image = null;
        job.ConfirmedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // İLİŞKİ ALGILAMA BURADA DA KOŞUYOR.
        //
        // `POST /rows` satırları yazdıktan sonra algılamayı çalıştırıyor, onay yolu
        // çalıştırmıyordu. Profil önbelleği bayatlamıyordu (bu uç `UpdatedAt`i
        // güncelliyor) ama yeni ilişki hiç KEŞFEDİLMİYORDU: kullanıcı belgeyle bir sete
        // `musteri_kodu` kolonu eklese ve o kolon "Müşteriler" setine işaret etse bile
        // hiçbir zaman profillenmiyor, doğal dilde sorgu iki seti birleştiremiyordu —
        // oysa aynı veri CSV ile yüklenseydi bağ kendiliğinden bulunacaktı. İki kapıdan
        // giren aynı veri iki farklı sonuç üretmemeli.
        //
        // Hata yutuluyor: algılama bir kolaylık, kaydetmeyi BOZMAMALI (içe aktarmadaki
        // kararın aynısı).
        try
        {
            await _relationDetector.DetectAsync(datasetId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "İlişki algılama başarısız (kaydetme etkilenmedi).");
        }

        return Ok(new DocumentConfirmResponse(datasetId, validation.ValidRows.Count,
            dataset.RowCount));
    }

    /// <summary>
    /// Belgeden çıkan tabloyu BAŞKA bir veri setinin şemasına hizalar — belge yeniden
    /// okunmadan.
    ///
    /// Onay ekranında kullanıcı hedefi değiştirdiğinde çağrılır. İkinci bir model çağrısı
    /// yapılmıyor: elde zaten bir tablo var, sorulan tek şey o tablonun yeni şemaya nasıl
    /// oturacağı — bu da saniyeler değil milisaniyeler süren bir ad karşılaştırması.
    ///
    /// Eşleme kuralı sunucuda kalıyor (Türkçe ad çözümlemesi, iyelik eki, tip uyumu);
    /// istemciye taşınsaydı aynı kural iki dilde iki kez yazılır ve zamanla ayrışırdı.
    /// </summary>
    // Rol kısıtı kardeş uçlarıyla (extract/confirm/discover) aynı. Eksikti: uç salt okuma
    // olduğu ve Viewer aynı şemayı GET /schema ile zaten görebildiği için sızıntı
    // doğurmuyordu, ama onay ekranı bir veri GİRİŞİ akışı — niyet tutarlı olmalı.
    [HttpPost("datasets/{datasetId:guid}/document/align")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Align(Guid datasetId, DocumentAlignRequest request,
        CancellationToken ct)
    {
        var dataset = await _db.Datasets.FirstOrDefaultAsync(d => d.Id == datasetId, ct);
        if (dataset is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Veri seti bulunamadı.");

        var columns = request.Columns ?? Array.Empty<string>();
        var rows = request.Rows ?? Array.Empty<string[]>();

        if (columns.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Hizalanacak kolon yok.");

        var schema = await _db.DatasetColumns
            .Where(c => c.DatasetId == datasetId)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnSchema(c.Name, c.Type))
            .ToListAsync(ct);

        if (schema.Count == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Bu veri setinin şeması tanımlı değil.");

        // Tipler değerlerden algılanıyor: tip uyuşmazlığını (belgede metin, sette tarih)
        // ancak böyle işaretleyebiliriz. Satır gelmezse her kolon metin sayılır.
        //
        // Hücreler Confirm ile AYNI normalleştirmeden geçiyor. İkisi ayrışsaydı ekran bir
        // tip gösterip kaydetme başka bir tiple yazardı — kullanıcının gördüğü ile
        // kaydedilenin ayrılması, bu akışta zaten bir kez bulunmuş olan kusur.
        var discovered = _importService.DetectSchema(
            new ParsedTable(columns, NormalizeCells(rows)));

        return Ok(DocumentAlignment.From(
            new DatasetSchema(datasetId, dataset.Name, schema), discovered));
    }

    /// <summary>
    /// Belgeyi ŞEMASIZ okuma işini (keşif geçişi) kuyruğa alır — hangi veri setine ait
    /// olabileceği iş bitince önerilecek.
    ///
    /// Bu uç neden var: `extract` hedef seti bilmek zorunda. İlk kez görülen bir belge
    /// türünde böyle bir set yoktur — kullanıcıya "önce elle set açın, kolonlarını yazın,
    /// sonra belgeyi yükleyin" demek, belgeden veri çıkarmanın bütün anlamını götürürdü.
    /// </summary>
    [HttpPost("documents/discover")]
    [Authorize(Roles = "Editor,Admin")]
    public async Task<IActionResult> Discover(IFormFile? file, CancellationToken ct)
    {
        if (ValidateUpload(file) is { } error) return error;

        return await QueueAsync(DocumentJobKind.Discover, datasetId: null, file!, ct);
    }

    /// <summary>
    /// İki geçişin ORTAK kuyruğa alma yolu: görüntüyü saklanacak hâle getir, iş kaydını
    /// aç, kuyruğa bırak.
    /// </summary>
    /// Sıra gevşetilemez: kayıt ÖNCE veritabanına yazılır, kuyruğa SONRA bırakılır. Tersi
    /// olsaydı işçi, kaydı henüz görünmeyen bir işi çalıştırmaya kalkar ve "bulunamadı"
    /// diyerek sessizce düşerdi.
    private async Task<IActionResult> QueueAsync(string kind, Guid? datasetId, IFormFile file,
        CancellationToken ct)
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return Problem(statusCode: StatusCodes.Status401Unauthorized,
                title: "Firma kimliği bulunamadı.");

        if (!Guid.TryParse(User.FindFirstValue("sub"), out var userId))
            return Problem(statusCode: StatusCodes.Status401Unauthorized,
                title: "Kullanıcı kimliği bulunamadı.");

        StoredImage stored;
        try
        {
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);

            var contentType = DocumentImagePrep.ContentTypeFor(
                Path.GetExtension(file.FileName));

            stored = await DocumentImagePrep.PrepareForStorageAsync(
                buffer.ToArray(), contentType, StoredImageLongEdge, ct);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            // Uzantı doğruydu ama içerik görüntü değil. Kuyruğa almak, kullanıcıyı boşuna
            // bekletip aynı hatayı iki dakika sonra vermek olurdu.
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Görüntü okunamadı; dosya bozuk olabilir.");
        }

        var job = new DocumentJob
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Kind = kind,
            DatasetId = datasetId,
            Status = DocumentJobStatus.Queued,
            Image = stored.Bytes,
            ImageContentType = stored.ContentType,
            FileName = file.FileName
        };

        _db.DocumentJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        _jobs.Enqueue<IDocumentJobRunner>(runner => runner.RunAsync(job.Id));

        // 202: "aldım, henüz bitmedi". 200 dönmek, sonucun hazır olduğu izlenimini verirdi.
        return Accepted(DocumentJobMapper.ToResponse(job));
    }

    /// <summary>Onay ekranından gelen hücreleri kanonik biçime çevirir.</summary>
    ///
    /// Kod incelemesinde bulunan sessiz bozulmanın kapanışı. <c>Normalize</c> bugüne kadar
    /// yalnız MODELİN çıktısına, yani ayrıştırma anında uygulanıyordu. Onay ekranı ise
    /// hücreleri düzenlenebilir yapıyor ve kullanıcının yazdığı metin sunucuya ham
    /// geliyordu. Kolonun tip/kültür kararı BÜTÜN değerlere birden bakılarak verildiği
    /// için tek bir Türkçe yazım kolonun tamamını çeviriyordu:
    ///
    ///   model "1993.32" ve "721.83" üretti (ikisi de doğru), kullanıcı üçüncü satırı
    ///   "1.234,56" diye düzeltti → kolonda virgül belirdi → kültür tr-TR seçildi →
    ///   dokunulmayan iki satır 199332 ve 72183 olarak yazıldı. Yüz kat sapma, tek bir
    ///   hata mesajı bile çıkmadan.
    ///
    /// Tehlike <c>DocumentExtractionParser.Normalize</c>'ın kendi yorumunda birebir tarif
    /// edilmişti; eksik olan, korumanın İKİNCİ giriş yoluna — kullanıcının klavyesine —
    /// uygulanmasıydı. CSV'de bu yol yok, çünkü orada bir kolonu tek araç yazar.
    ///
    /// Normalize boş/null hücrede null döner; <c>ParsedTable</c> null taşımadığı için boş
    /// metne indiriliyor — doğrulama katmanı boş hücreyi zaten "değer yok" sayıyor.
    private static List<string[]> NormalizeCells(IReadOnlyList<string[]> rows) =>
        rows.Select(r => r
                .Select(cell => DocumentExtractionParser.Normalize(cell) ?? string.Empty)
                .ToArray())
            .ToList();

    /// <summary>
    /// Kullanıcının eklenmesini istediği kolonları doğrular ve tiplerini değerlerden algılar.
    /// Sorun varsa <paramref name="error"/> dolar ve false döner.
    /// </summary>
    /// Bu yol, "eşleşmeyen kolon ne olacak" sorusunun üçüncü cevabı: eşle, at, ya da SETE
    /// EKLE. JSONB veri modeli sayesinde eklemenin bedeli yok — var olan satırlarda o alan
    /// hiç bulunmuyor, yani veri göçü gerekmiyor, yalnız şema kaydı yazılıyor.
    private bool BuildNewColumns(IReadOnlyList<string>? requested, ParsedTable table,
        IReadOnlyList<ColumnSchema> schema, out List<ColumnSchema> added, out IActionResult? error)
    {
        added = new List<ColumnSchema>();
        error = null;

        if (requested is null || requested.Count == 0) return true;

        if (requested.Count > MaxNewColumns)
        {
            error = Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"Tek belgeyle en fazla {MaxNewColumns} yeni kolon eklenebilir.");
            return false;
        }

        // Tipler bir kez, TABLONUN TAMAMI üzerinden algılanıyor: kolon başına ayrı çağrı
        // yapmak aynı işi tekrarlardı ve içe aktarmadaki kuralla ayrışma riski doğardı.
        var detected = _importService.DetectSchema(table)
            .ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);

        var existing = schema.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in requested)
        {
            var name = raw?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                error = Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Yeni kolon adı boş olamaz.");
                return false;
            }

            // Uzunluk denetimi: kolon adı veritabanında varchar(200). Denetlenmediğinde
            // uzun bir ad (ör. kutuya yapıştırılmış bir açıklama) doğrulamayı geçiyor ve
            // SaveChanges "value too long for type character varying(200)" ile düşüyordu —
            // kullanıcı 400 yerine hangi alanın sorunlu olduğunu söylemeyen bir 500
            // alıyordu. Veri yazılmıyordu (SaveChanges atomik), ama sebep de görünmüyordu.
            if (name.Length > MaxColumnNameLength)
            {
                error = Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: $"Kolon adı en fazla {MaxColumnNameLength} karakter olabilir.");
                return false;
            }

            // Adı sette zaten varsa ekleme değil EŞLEME yapılmalıydı; eklemek aynı ada
            // sahip iki kolon bırakır ve hangisinin okunacağı belirsizleşir.
            if (!existing.Add(name))
            {
                error = Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: $"\"{name}\" adında bir kolon zaten var; yeni kolon olarak eklenemez.");
                return false;
            }

            // Gönderilen tabloda karşılığı olmayan bir kolon eklemek, her satırı boş olan
            // bir alan açmak olurdu.
            if (!detected.TryGetValue(name, out var type))
            {
                error = Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: $"\"{name}\" kolonu gönderilen tabloda bulunmuyor.");
                return false;
            }

            added.Add(new ColumnSchema(name, type));
        }

        return true;
    }

    private IActionResult? ValidateUpload(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya boş veya gönderilmedi.");

        if (file.Length > MaxUploadBytes)
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Dosya 15 MB sınırını aşıyor.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Yalnızca .jpg, .jpeg, .png ve .webp görüntüleri desteklenir.");

        return null;
    }
}
