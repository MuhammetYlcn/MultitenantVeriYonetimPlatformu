using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeriYonetim.Api.Data;
using VeriYonetim.Api.Models.Entities;

namespace VeriYonetim.Api.Services;

// Kolon indekslemesinin sonucu. Kurulamayan bir indeks hata değil, açıklanması gereken
// bir cevaptır: kullanıcı neden hızlanmadığını görmeli.
public record IndexResult(bool Created, string? Reason, double Seconds);

public interface IDatasetIndexService
{
    Task<IndexResult> CreateAsync(Guid datasetId, string columnName, CancellationToken ct = default);
    Task<bool> DropAsync(Guid datasetId, string columnName, CancellationToken ct = default);
    Task<HashSet<string>> IndexedColumnsAsync(Guid datasetId, CancellationToken ct = default);

    /// <summary>
    /// Önerilen şemanın, platformda duran bir kolon indeksiyle tip çakışması var mı.
    /// Varsa kullanıcıya gösterilecek sebebi döndürür, yoksa null.
    /// </summary>
    Task<string?> SchemaConflictAsync(
        IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default);
}

// Kolon bazlı ifade indekslerini kurar ve düşürür.
//
// İndekslenen ifade, sorgunun ürettiği ifadeyle KARAKTER KARAKTER aynı olmalıdır; yoksa
// PostgreSQL indeksi hiç kullanmaz ve kullanıcı hızlandırdığını sanır. Bu yüzden
// ifadeler DatasetSqlExpr'in ürettiklerinden türetildi (metin karşılaştırması `lower()`
// altında yapılıyor, sayısal karşılaştırma `::numeric` altında).
public class DatasetIndexService : IDatasetIndexService
{
    /// <summary>
    /// İndeks komutlarının üst süre sınırı.
    ///
    /// 30 dakikadan 5 dakikaya indirildi. Eski değer bir kaçış yolu bırakmıyordu: kilit
    /// bekleyen bir DROP, arkasına biriken bütün firmaların sorgularını yarım saate kadar
    /// bekletebilirdi. 1 milyon satırda ölçülen kurulum süresi ~40 saniye, yani 5 dakika
    /// gerçek işin çok üstünde bir tavan.
    /// </summary>
    private const int CommandTimeoutSeconds = 300;

    private readonly AppDbContext _db;
    private readonly ILogger<DatasetIndexService> _logger;

    public DatasetIndexService(AppDbContext db, ILogger<DatasetIndexService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IndexResult> CreateAsync(
        Guid datasetId, string columnName, CancellationToken ct = default)
    {
        var column = await _db.DatasetColumns
            .Where(c => c.DatasetId == datasetId && c.Name == columnName)
            .Select(c => new { c.Name, c.Type })
            .FirstOrDefaultAsync(ct);

        if (column is null)
            return new IndexResult(false, $"'{columnName}' bu veri setinde yok.", 0);

        // Tarih kolonları indekslenemiyor ve bu bir eksiklik değil, PostgreSQL'in
        // kuralı: metinden tarihe dönüşüm DateStyle ayarına bağlı olduğu için IMMUTABLE
        // sayılmıyor, IMMUTABLE olmayan ifade de indekslenemiyor. 24.08 ölçümünde bu
        // aday denendi ve tam olarak bu hatayla düştü. Kullanıcıya sessizce "kuruldu"
        // demektense sebebi söyleniyor.
        if (Expression(column.Name, column.Type) is not { } expression)
            return new IndexResult(false,
                column.Type == "date"
                    ? "Tarih kolonları indekslenemiyor: metinden tarihe dönüşüm " +
                      "veritabanı ayarına bağlı olduğundan indekse konamaz."
                    : $"'{column.Type}' tipi indekslenmiyor.",
                0);

        if (await _db.DatasetIndexes
                .AnyAsync(i => i.DatasetId == datasetId && i.ColumnName == column.Name, ct))
            return new IndexResult(false, "Bu kolon zaten indeksli.", 0);

        // TİP ÇAKIŞMASI. Satırlar tek bir tabloda durduğu için indeks de tablo
        // genelindedir: `tutar` kolonunu sayı olarak indekslemek, tabloda `tutar` adını
        // taşıyan BÜTÜN satırların sayıya çevrilmesini gerektirir. Aynı adı metin kolon
        // olarak kullanan başka bir veri seti varsa (ki değerleri "1.500,50" gibi
        // olabilir) indeks kurulurken veritabanı hata verir.
        //
        // Bu, denenmeden görülmeyen bir kusurdu: ölçüm ortamında her set aynı şemadan
        // türetildiği için hiç çıkmadı, gerçek veride ilk denemede çıktı. Kontrol burada
        // ve şema üzerinden yapılıyor — hem ucuz hem de sebebi söylenebilir.
        var conflicting = await _db.DatasetColumns
            .IgnoreQueryFilters()
            .AnyAsync(c => c.Name == column.Name && c.Type != column.Type, ct);

        // MESAJ DİĞER FİRMANIN TİPİNİ SÖYLEMİYOR.
        //
        // Eskiden söylüyordu ve o, platformdaki bütün firmaların şema metadata'sına karşı
        // bir sorgulama aracıydı: kendi setine tek kolonluk bir şema yükleyip indeks
        // istemek, "bu adda bir kolon var mı, tipi ne" sorusuna cevap veriyordu. Kolon
        // adını değiştirerek (vergi_no, tckn, hasta_adi, maas…) bütün ad uzayı taranabilir
        // ve hangi firmaların hangi hassas alanları tuttuğu çıkarılabilirdi. Satır verisi
        // sızmıyordu ama şema bilgisi de müşteri bilgisidir.
        //
        // Burada tipi söylemeye gerek de yok: kullanıcının yapacağı iş kolonu yeniden
        // adlandırmak. (Şema yazma yolunda — SchemaConflictAsync — tip SÖYLENİYOR, çünkü
        // orada kullanıcı verisini uydurması gereken tipi bilmek zorunda.)
        if (conflicting)
            return new IndexResult(false,
                $"'{column.Name}' adı platformda farklı bir tiple kullanıldığı için " +
                "indekslenemiyor. Satırlar tek bir tabloda durduğundan indeks de " +
                "ortaktır; aynı ad iki farklı tiple indekslenemez. Kolonu yeniden " +
                "adlandırıp tekrar deneyebilirsiniz.", 0);

        var indexName = IndexName(column.Name, column.Type);

        // Kurulumun TAMAMI (geçersiz indeks temizliği + CREATE + kayıt yazımı) tek bir
        // danışma kilidinin altında. Aynı indeks üzerinde çalışan başka bir istek —
        // kullanıcının ikinci tıklaması ya da aynı kolonu indeksleyen başka bir firma —
        // burada bekler. Kilit olmadan üç ayrı yarış durumu açıktı (bkz. WithIndexLockAsync).
        return await WithIndexLockAsync(indexName,
            () => CreateLockedAsync(datasetId, column.Name, column.Type, indexName,
                expression, ct), ct);
    }

    private async Task<IndexResult> CreateLockedAsync(Guid datasetId, string columnName,
        string columnType, string indexName, string expression, CancellationToken ct)
    {
        // Kilidi bekledikten sonra durum değişmiş olabilir: bizden önce sıraya girmiş bir
        // istek aynı kolonu indekslemiş olabilir. Yeniden bakılıyor.
        if (await _db.DatasetIndexes
                .AnyAsync(i => i.DatasetId == datasetId && i.ColumnName == columnName, ct))
            return new IndexResult(false, "Bu kolon zaten indeksli.", 0);

        // Fiziksel indeks başka bir setin kaydı yüzünden zaten duruyor olabilir; o zaman
        // yalnız kayıt eklenir. IF NOT EXISTS bunu veritabanına da doğrulatıyor.
        // Önceki bir denemeden kalmış GEÇERSİZ indeks varsa önce o temizlenir. Aksi
        // halde aşağıdaki IF NOT EXISTS onu "zaten var" sayıp atlar, kayıt eklenir ve
        // kullanıcı hızlandırdığını sanar — oysa sorgular o indeksi kullanamaz.
        //
        // Kilit sayesinde burada "kurulmakta olan" bir indeksi yarım sanma tehlikesi de
        // yok: aynı ad üzerinde aynı anda ikinci bir kurulum koşamıyor.
        await DropIfInvalidAsync(indexName, ct);

        var started = DateTime.UtcNow;

        try
        {
            await CreateIndexAsync(indexName, expression, ct);
        }
        catch (PostgresException ex)
        {
            // CREATE INDEX CONCURRENTLY başarısız olduğunda arkasında geçersiz bir
            // indeks bırakır; temizlenmezse bir sonraki deneme onu var sayar.
            await DropIfInvalidAsync(indexName, ct);

            if (ex.SqlState != PostgresErrorCodes.InvalidTextRepresentation) throw;

            // Şema kontrolünü geçmiş ama satırda yine de çevrilemeyen bir değer var:
            // şemanın söylediğiyle satırda duranın ayrıştığı tek yer burası olabilir.
            // İsteği çökertmek yerine ne bulunduğu söyleniyor.
            //
            // İSTİSNANIN KENDİSİ LOGA YAZILMIYOR. PostgreSQL'in bu hata sınıfındaki
            // mesajı sorunlu değeri metin olarak taşır ("invalid input syntax for type
            // numeric: ..."), yani `LogWarning(ex, ...)` müşteri hücresini sunucu
            // günlüğüne düşürüyordu — izolasyon düzeneğinin dışında ikinci bir kopya.
            // Olay Warning'de kalıyor, İÇERİK Debug'a iniyor: 26.08'de dört yerde
            // uygulanan ayrımın aynısı (Logging:LogLevel:VeriYonetim = "Debug" geri getirir).
            _logger.LogWarning(
                "Kolon indekslenemedi, veri tipe uymuyor: {Column} ({SqlState})",
                columnName, ex.SqlState);
            _logger.LogDebug(ex, "İndekslenemeyen kolonun veritabanı hatası: {Column}",
                columnName);

            // Kullanıcıya dönen metinden de çıkarıldı: aynı ham değer 400 gövdesinde
            // taşınıyordu. Kolon ve tip, sorunu düzeltmek için zaten yeterli.
            return new IndexResult(false,
                $"'{columnName}' kolonunda {columnType} tipine çevrilemeyen bir değer " +
                "var; kolonu düzeltip tekrar deneyin.", 0);
        }

        var seconds = (DateTime.UtcNow - started).TotalSeconds;

        _db.DatasetIndexes.Add(new DatasetIndex
        {
            Id = Guid.NewGuid(),
            DatasetId = datasetId,
            ColumnName = columnName,
            ColumnType = columnType,
            IndexName = indexName
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Kayıt yazılamazsa fiziksel indeks ÖKSÜZ kalırdı: hiçbir kayıt ona işaret
            // etmediği için referans sayımı onu hiçbir zaman düşürmez ve o kolonun
            // indeksi silinse bile yazma maliyeti sonsuza kadar ödenmeye devam ederdi.
            // Kurulumu geri alıp hatayı yukarı taşıyoruz.
            _db.ChangeTracker.Clear();
            await DropIfUnusedAsync(indexName, ct);
            throw;
        }

        _logger.LogInformation(
            "Kolon indeksi kuruldu: {Column} ({Type}) → {Index}, {Seconds:F1} sn",
            columnName, columnType, indexName, seconds);

        // ANALYZE — indeksin GERÇEKTEN kullanılması için.
        //
        // Ölçüm aracı bunu bilerek yapıyordu ve gerekçesini de yazmıştı ("ANALYZE
        // atlanırsa ilk koşular indekssizmiş gibi ölçülür"), ama üretim yolunda adım yoktu.
        // İfade indekslerinin seçicilik istatistiği yalnız ANALYZE ile toplanır; o gelene
        // kadar planlayıcı varsayılan tahminle çalışır ve Seq Scan seçmeye devam edebilir.
        // Kullanıcı "indekslendi, 12,4 sn" cevabını alıp panonun hâlâ eski hızda koştuğunu
        // görüyor, kod tarafında yanlış bir şey görünmüyordu. Yazma olmayan bir sette
        // autovacuum'un analyze eşiği hiç dolmayabilir, yani kendiliğinden de gelmez.
        await AnalyzeAsync(ct);

        return new IndexResult(true, null, seconds);
    }

    public async Task<bool> DropAsync(
        Guid datasetId, string columnName, CancellationToken ct = default)
    {
        var record = await _db.DatasetIndexes
            .FirstOrDefaultAsync(i => i.DatasetId == datasetId && i.ColumnName == columnName, ct);

        if (record is null) return false;

        // Kayıt silme + referans sayımı + fiziksel düşürme AYNI kilidin altında.
        // Ayrıldıklarında araya giren bir kurulum "indeks zaten var" görüp yalnız kayıt
        // ekliyor, ardından buradaki DROP tamamlanıyor ve ortada kaydı olan ama fiziksel
        // karşılığı olmayan bir indeks kalıyordu.
        return await WithIndexLockAsync(record.IndexName, async () =>
        {
            _db.DatasetIndexes.Remove(record);
            await _db.SaveChangesAsync(ct);

            await DropIfUnusedAsync(record.IndexName, ct);

            return true;
        }, ct);
    }

    /// <summary>
    /// Şema yazılmadan ÖNCE, platformdaki kolon indeksleriyle tip çakışması aranır.
    ///
    /// Kod incelemesinde bulunan kusurun kapanışı: çakışma denetimi YALNIZCA indeks
    /// kurulurken yapılıyordu, ters yön — indeks zaten dururken aynı adı farklı tiple
    /// taşıyan yeni bir şema kaydedilmesi — hiç denetlenmiyordu.
    ///
    /// Fiziksel indeks tablo geneli olduğu için (`("DatasetId", (("Data"->>'tutar'))::numeric)`)
    /// tablodaki HERHANGİ bir satıra `tutar` anahtarıyla sayı olmayan bir değer yazmak
    /// indeks ifadesinin değerlendirilmesini düşürür. Somut sonucu şuydu:
    ///
    ///   A firması `tutar`ı (number) indeksliyor → indeks tablo genelinde kuruluyor.
    ///   B firması `tutar` sütununda "1.500,50 TL" yazan bir CSV yüklüyor → tip text
    ///   algılanıyor, şema 200 dönüyor. Satırları yazarken COPY, PostgreSQL indeks
    ///   ifadesini değerlendirdiği için `invalid input syntax for type numeric` ile
    ///   düşüyor ve B firması BOŞ BİR 500 alıyor. O dosyayı bir daha asla içeri
    ///   alamıyor: sebebi göremiyor, kendi tarafında düzeltemiyor, A'nın indeksine de
    ///   erişemiyor.
    ///
    /// Sorgu bilinçli olarak çapraz-kiracı (`IgnoreQueryFilters`): sorulan şey "benim
    /// setlerim" değil, "bu ad platformda başka bir tiple indekslenmiş mi". Buna karşılık
    /// MESAJ diğer firmanın tipini SÖYLEMİYOR — eski mesaj söylüyordu ve o, platformdaki
    /// bütün firmaların kolon adlarını ve tiplerini sorgulamaya yarayan bir araçtı
    /// (kolon adını değiştirip denemek yeterliydi). Kullanıcının kendi tarafında yapacağı
    /// iş için kolon adı zaten yeterli bilgi.
    /// </summary>
    public async Task<string?> SchemaConflictAsync(
        IReadOnlyList<ColumnSchema> schema, CancellationToken ct = default)
    {
        if (schema.Count == 0) return null;

        var names = schema.Select(c => c.Name).ToList();

        var indexed = await _db.DatasetIndexes
            .IgnoreQueryFilters()
            .Where(i => names.Contains(i.ColumnName))
            .Select(i => new { i.ColumnName, i.ColumnType })
            .Distinct()
            .ToListAsync(ct);

        if (indexed.Count == 0) return null;

        var byName = schema.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);

        var clash = indexed.FirstOrDefault(i =>
            byName.TryGetValue(i.ColumnName, out var type) && type != i.ColumnType);

        if (clash is null) return null;

        return $"'{clash.ColumnName}' kolonu platformda '{clash.ColumnType}' tipiyle "
               + $"indekslenmiş, sizin dosyanızda ise '{byName[clash.ColumnName]}' olarak "
               + "algılandı. Satırlar tek bir tabloda durduğundan indeks de ortaktır ve "
               + "bu dosya yazılamaz. Kolonun adını değiştirin ya da değerleri "
               + $"'{clash.ColumnType}' tipine uyacak biçimde düzeltin.";
    }

    public async Task<HashSet<string>> IndexedColumnsAsync(
        Guid datasetId, CancellationToken ct = default) =>
        (await _db.DatasetIndexes
            .Where(i => i.DatasetId == datasetId)
            .Select(i => i.ColumnName)
            .ToListAsync(ct))
        .ToHashSet();

    // Fiziksel indeksi yalnız SON kayıt gidince düşür: aynı kolon adını kullanan başka
    // bir veri seti — başka bir firmanınki de olabilir — hâlâ ondan faydalanıyor olabilir.
    // Query filter bilinçli olarak atlanıyor; sorulan şey "benim setlerim" değil,
    // "bu indekse dayanan başka kayıt kaldı mı".
    private async Task DropIfUnusedAsync(string indexName, CancellationToken ct)
    {
        var stillUsed = await _db.DatasetIndexes
            .IgnoreQueryFilters()
            .AnyAsync(i => i.IndexName == indexName, ct);

        if (stillUsed) return;

        await ExecuteOutsideTransactionAsync($"DROP INDEX CONCURRENTLY IF EXISTS {indexName}", ct);
        _logger.LogInformation("Kolon indeksi düşürüldü: {Index}", indexName);
    }

    // Yarım kalmış bir indeksi düşürür. PostgreSQL, CONCURRENTLY kurulumu başarısız
    // olduğunda indeksi silmez; `indisvalid = false` olarak bırakır. Böyle bir indeks
    // sorgularda kullanılmaz ama adı doludur — yani "zaten var" görünür.
    private async Task DropIfInvalidAsync(string indexName, CancellationToken ct)
    {
        var connectionString = _db.Database.GetConnectionString();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText =
                """
                SELECT COUNT(*) FROM pg_index i
                JOIN pg_class c ON c.oid = i.indexrelid
                WHERE c.relname = @name AND NOT i.indisvalid
                """;
            check.Parameters.AddWithValue("name", indexName);

            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) == 0) return;
        }

        // CONCURRENTLY — düz DROP INDEX değil.
        //
        // Aynı dosyada aynı iş iki farklı kilit davranışıyla yapılıyordu: düşürme
        // yolunda CONCURRENTLY vardı, buradaki temizlikte yoktu. Düz DROP INDEX
        // "DatasetRows" üzerinde ACCESS EXCLUSIVE kilit ister ve PostgreSQL'de kilit
        // kuyruğu adildir — arkasına gelen HER sorgu (bütün firmaların satır listeleri,
        // panoları, içe aktarmaları) beklemek zorunda kalır. Bu, dosyanın kendi tasarım
        // gerekçesinin ("düz CREATE bütün firmaların yazmasını bekletirdi") tam tersiydi.
        // CONCURRENTLY geçersiz indeksler üzerinde de çalışıyor.
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP INDEX CONCURRENTLY IF EXISTS {indexName}";
        drop.CommandTimeout = CommandTimeoutSeconds;
        await drop.ExecuteNonQueryAsync(ct);

        _logger.LogWarning("Yarım kalmış indeks temizlendi: {Index}", indexName);
    }

    /// İndeks kurulduktan sonra istatistik toplar. Ayrı bağlantıda, çünkü ANALYZE de
    /// uzun sürebilir ve isteğin işlemine bağlanmaması gerekiyor.
    private Task AnalyzeAsync(CancellationToken ct) =>
        ExecuteOutsideTransactionAsync("""ANALYZE "DatasetRows" """, ct);

    private Task CreateIndexAsync(string indexName, string expression, CancellationToken ct) =>
        ExecuteOutsideTransactionAsync(
            $"""CREATE INDEX CONCURRENTLY IF NOT EXISTS {indexName} ON "DatasetRows" ("DatasetId", {expression})""",
            ct);

    // CONCURRENTLY, indeks kurulurken tabloya YAZILABİLMESİNİ sağlar. Bu kaçınılmaz:
    // satırlar tek bir tabloda durduğundan, düz bir CREATE INDEX o süre boyunca
    // BÜTÜN firmaların içe aktarmasını bekletirdi (2,2 milyon satırlık ölçüm tablosunda
    // 1,7-17 saniye). Bedeli, komutun bir işlemin içinde çalışamaması — bu yüzden
    // kendi bağlantısını açıyor.
    private async Task ExecuteOutsideTransactionAsync(string sql, CancellationToken ct)
    {
        var connectionString = _db.Database.GetConnectionString();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Bir indeks adı üzerindeki bütün işlemleri SERİ hâle getirir.
    ///
    /// Kod incelemesinde bu dosyada üç ayrı yarış durumu bulundu ve üçünün de kökü aynı:
    /// fiziksel indeks (CONCURRENTLY, kendi bağlantısında, işlem DIŞINDA) ile kayıt
    /// (EF, kendi işleminde) iki ayrı adımda yapılıyor ve aralarında hiçbir kilit yok.
    ///
    ///   • Silme: kayıt siliniyor, sayım 0 bulunuyor, DROP başlıyor. Tam o aralıkta başka
    ///     bir firma aynı kolonu indeksliyor → `CREATE ... IF NOT EXISTS` indeks HÂLÂ
    ///     durduğu için hiçbir şey yapmadan dönüyor, kayıt ekleniyor, kullanıcıya
    ///     "indekslendi" deniyor. Ardından DROP tamamlanıyor: kayıt var, indeks yok.
    ///     Kullanıcı hızlandırdığını sanıyor, sorgular yavaş ve "zaten indeksli" cevabı
    ///     düzeltmeyi de engelliyor.
    ///   • Çift tıklama: 1 milyon satırda kurulum 40 saniye sürüyor, kullanıcı ikinci kez
    ///     basıyor; ikinci istek `DropIfInvalidAsync`'te KURULMAKTA OLAN indeksi
    ///     "yarım kalmış" sanıp düşürüyor ve birinci isteği öldürüyor.
    ///   • Kayıt yazımı düşerse fiziksel indeks öksüz kalıyor.
    ///
    /// Danışma kilidi (advisory lock) OTURUM düzeyinde ve bu bağlantıya bağlı; işlem
    /// gerektirmediği için CONCURRENTLY ile birlikte kullanılabiliyor — normal bir tablo
    /// kilidi burada işe yaramazdı. Anahtar indeks adının özetinden türetiliyor, yani
    /// yalnız AYNI indeks üzerindeki işlemler birbirini bekliyor; farklı kolonlar paralel
    /// kurulmaya devam ediyor.
    /// </summary>
    private async Task<T> WithIndexLockAsync<T>(string indexName,
        Func<Task<T>> action, CancellationToken ct)
    {
        var connectionString = _db.Database.GetConnectionString();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var key = LockKey(indexName);

        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText = "SELECT pg_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", key);
            acquire.CommandTimeout = CommandTimeoutSeconds;
            await acquire.ExecuteNonQueryAsync(ct);
        }

        try
        {
            return await action();
        }
        finally
        {
            // Kilit oturuma bağlı olduğu için bağlantı kapanınca zaten düşer; yine de
            // açıkça bırakılıyor ki havuza dönen bağlantı kilidi taşımasın.
            await using var release = connection.CreateCommand();
            release.CommandText = "SELECT pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", key);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// İndeks adından 64 bitlik kilit anahtarı. Ad zaten SHA-256 özetinin ilk 16 hex
    /// hanesi olduğundan çakışma ihtimali yok denecek kadar düşük.
    private static long LockKey(string indexName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(indexName));
        return BitConverter.ToInt64(hash, 0);
    }

    // İndekslenecek ifade. Tip desteklenmiyorsa null.
    private static string? Expression(string column, string type) => type switch
    {
        // Metin eşitliği harfe duyarsız yapılıyor (DatasetSqlExpr'deki karar); indeks de
        // aynı ifadeye kurulmalı, ham değere kurulan indeks o sorguda kullanılmaz.
        "text" => $"lower((\"Data\"->>'{Escape(column)}'))",
        "number" => $"(((\"Data\"->>'{Escape(column)}'))::numeric)",
        _ => null
    };

    // İndeks adı kolon adından TÜRETİLMEZ, özetlenir. Sebebi iki katlı: PostgreSQL
    // tanımlayıcıları 63 baytla sınırlı (uzun ya da Türkçe karakterli bir kolon adı
    // sessizce kırpılıp iki kolonu aynı ada düşürebilirdi), ve ad SQL metnine gömüldüğü
    // için kolon adından gelen hiçbir karakterin oraya ulaşmaması gerekir.
    private static string IndexName(string column, string type)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{type}:{column}"));
        return $"ix_rows_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    // İfadenin içindeki kolon adı parametre olamaz (indeks tanımının parçası), bu yüzden
    // tek tırnaklar ikiye katlanıyor — RelationDetector'daki profilleme sorgusuyla aynı
    // kural. Kolon adının kendisi ayrıca şemadan doğrulanarak geliyor, uydurulmuş bir
    // ad buraya kadar ulaşamaz.
    private static string Escape(string name) => name.Replace("'", "''");
}
