using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace VeriYonetim.Api.Services;

// Bir kolonun hücrelerinin NASIL okunacağı. Karar kolonun tamamına bakılarak bir kez
// verilir, hücre hücre değil: "03.04.2026" tek başına belirsizdir (3 Nisan mı, 4 Mart mı),
// ama kolonda "13.04.2026" de varsa gün-ay sırası kesinleşir.
//
// Aynı nesne HEM şema algılamada HEM satır dönüştürmede kullanılır. İkisi ayrı kurallara
// göre çalışırsa kolon "date" algılanır ama satırlar dönüştürmede elenir — yani veri
// sessizce kaybolur. Bu yüzden tek kaynak: ValueFormats.
internal sealed record ColumnFormat(string Type, CultureInfo Culture, string? DateFormat);

// Ham metin değerlerinden tip ve biçim çıkaran ortak katman.
//
// Burası kasıtlı olarak dosya biçiminden bağımsızdır: CSV, Excel ve ileride başka bir
// kaynak (ör. yapay zekâ ile belgeden çıkarılan tablo) hepsi ParsedTable'a indirgenip
// buradan geçer. Tarih/sayı tanımı tek yerde durur, kaynak başına çoğalmaz.
internal static class ValueFormats
{
    internal static readonly CultureInfo Turkish = new("tr-TR");
    internal static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Kabul edilen tarih biçimleri, ÖNCELİK SIRASIYLA denenir.
    // Not: "MM/dd/yyyy" (ABD sırası) bilerek YOK. "04/03/2026" ile "dd/MM/yyyy" ayırt
    // edilemez; ikisini birden desteklemek belirsizliği çözmez, yalnız hangi yanlışın
    // seçileceğini rastlantıya bırakır. Ürün Türkçe olduğundan gün-ay sırası esas alınır.
    internal static readonly string[] DateFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy/MM/dd",
        "dd.MM.yyyy",
        "dd.MM.yyyy HH:mm",
        "dd.MM.yyyy HH:mm:ss",
        "dd/MM/yyyy",
        "dd-MM-yyyy",
    };

    // "1.500" iki türlü okunabilir: 1,5 (nokta ondalık) ya da 1500 (Türkçe binlik).
    // Ayırt edici kural: noktadan sonra TAM üç hane varsa binlik ayıracıdır.
    // "1.5", "1.25", "1.5000" ondalıktır; "1.500", "12.345.678" binliktir.
    private static readonly Regex ThousandsDot =
        new(@"^-?\d{1,3}(\.\d{3})+$", RegexOptions.Compiled);

    /// Kolonun tipini ve biçimini birlikte belirler.
    internal static ColumnFormat Detect(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return new ColumnFormat("text", Invariant, null);

        // Sayı tarihten ÖNCE denenir: "2026" hem sayı hem yıl gibi görünür, sayı sayılır.
        var numberCulture = DetectNumberCulture(values);
        if (numberCulture != null) return new ColumnFormat("number", numberCulture, null);

        var dateFormat = DetectDateFormat(values);
        if (dateFormat != null) return new ColumnFormat("date", Invariant, dateFormat);

        return new ColumnFormat("text", Invariant, null);
    }

    /// Kayıtlı şema tipi için biçimi yeniden bulur (satır yüklemede kullanılır).
    /// Tip zaten bilindiğinden yalnız biçim kararı tazelenir.
    internal static ColumnFormat For(string type, IReadOnlyList<string> values) => type switch
    {
        "number" => new ColumnFormat(
            "number", DetectNumberCulture(values) ?? Invariant, null),
        "date" => new ColumnFormat("date", Invariant, DetectDateFormat(values)),
        _ => new ColumnFormat("text", Invariant, null),
    };

    private static CultureInfo? DetectNumberCulture(IReadOnlyList<string> values)
    {
        // Virgül gören yerde karar nettir: Türkçe ondalık ("980,25", "1.500,50").
        if (values.Any(v => v.Contains(','))
            && values.All(v => decimal.TryParse(v, NumberStyles.Number, Turkish, out _)))
            return Turkish;

        // Virgül yok. Noktalar binlik ayıracı gibi duruyorsa Türkçe oku — yoksa
        // "1.500" InvariantCulture'da 1,5 olur ve sessizce yanlış veri üretirdi.
        if (values.Any(v => ThousandsDot.IsMatch(v))
            && values.All(v => decimal.TryParse(v, NumberStyles.Number, Turkish, out _)))
            return Turkish;

        if (values.All(v => decimal.TryParse(v, NumberStyles.Number, Invariant, out _)))
            return Invariant;

        return null;
    }

    // Kolonun BÜTÜN değerlerini ayrıştırabilen ilk biçim kazanır.
    private static string? DetectDateFormat(IReadOnlyList<string> values) =>
        DateFormats.FirstOrDefault(f => values.All(v => TryParseDate(v, f, out _)));

    internal static bool TryParseDate(string value, string? format, out DateTime result)
    {
        if (format != null)
            return DateTime.TryParseExact(value.Trim(), format, Invariant,
                DateTimeStyles.None, out result);

        // Biçim kararlaştırılamadıysa (şema başka bir dosyadan gelmiş olabilir) esnek dene.
        return DateTime.TryParse(value, Turkish, DateTimeStyles.None, out result)
            || DateTime.TryParse(value, Invariant, DateTimeStyles.None, out result);
    }

    internal static bool TryParseNumber(string value, CultureInfo culture, out decimal result) =>
        decimal.TryParse(value.Trim(), NumberStyles.Number, culture, out result);

    /// Başlık satırındaki ayraç adaylarını sayarak CSV ayracını sezer.
    /// Türkçe Windows Excel "CSV olarak kaydet" dediğinde ";" yazar; sezmezsek dosya
    /// tek kolon olarak okunur ve kullanıcı hata bile almaz.
    internal static string SniffDelimiter(string headerLine)
    {
        var candidates = new[] { ",", ";", "\t", "|" };
        var best = candidates
            .Select(d => (Delimiter: d, Count: headerLine.Split(d).Length - 1))
            .OrderByDescending(x => x.Count)
            .First();

        return best.Count > 0 ? best.Delimiter : ",";
    }
}

// CSV ve Excel'in ortak indirgendiği ara yapı: başlık satırı + ham veri satırları.
// Her hücre string'tir; tip algılama sonraki adımda bu ham değerlerden yapılır.
public record ParsedTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows);

// Algılanan kolon: adı + tipi ("text" | "number" | "date").
public record ColumnSchema(string Name, string Type);

// Tek bir hücrenin şema tipine uymadığını raporlar (satır 1 tabanlı; başlık sayılmaz).
public record RowError(int Row, string Column, string? Value, string ExpectedType);

// Satır validasyonunun sonucu: import edilecek geçerli satırlar (kolon adı → tipli değer)
// ve elenen hücrelerin hata listesi.
public record RowValidationResult(
    IReadOnlyList<Dictionary<string, object?>> ValidRows,
    IReadOnlyList<RowError> Errors);

public interface IDatasetImportService
{
    Task<ParsedTable> ParseCsvAsync(Stream stream);
    Task<ParsedTable> ParseExcelAsync(Stream stream);
    IReadOnlyList<ColumnSchema> DetectSchema(ParsedTable table);
    RowValidationResult ValidateRows(ParsedTable table, IReadOnlyList<ColumnSchema> schema);
}

public class DatasetImportService : IDatasetImportService
{
    public async Task<ParsedTable> ParseCsvAsync(Stream stream)
    {
        // Ayracı sezebilmek için başlık satırını önce ham olarak okumak gerekiyor.
        using var reader = new StreamReader(stream);
        var headerLine = await reader.ReadLineAsync()
            ?? throw new InvalidDataException("Dosya boş.");
        var delimiter = ValueFormats.SniffDelimiter(headerLine);

        // Akış başlığı tükettiği için baştan kurgulanır: başlık + kalan gövde.
        var body = await reader.ReadToEndAsync();
        using var full = new StringReader(headerLine + Environment.NewLine + body);

        // CsvParser: düşük seviye okuyucu — her satırı ham string[] olarak verir.
        // Tipli sınıfa map ETMİYORUZ; kolonlar önceden bilinmediğinden dinamik okuyoruz.
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
        };
        using var parser = new CsvParser(full, config);

        // İlk satır = başlık.
        if (!await parser.ReadAsync())
            throw new InvalidDataException("Dosya boş.");
        var headers = parser.Record!.ToArray();

        // Kalan satırlar = veri.
        var rows = new List<string[]>();
        while (await parser.ReadAsync())
            rows.Add(parser.Record!);

        return new ParsedTable(headers, rows);
    }

    public Task<ParsedTable> ParseExcelAsync(Stream stream)
    {
        // ClosedXML API'si senkron; imzayı diğerleriyle tutarlı tutmak için Task sarıyoruz.
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        var used = sheet.RangeUsed()
            ?? throw new InvalidDataException("Excel sayfası boş.");

        // Sabit kolon aralığı: başlıktaki boş hücreler yüzünden satır kayması olmasın diye
        // her satırı ilk..son kolon aralığında birebir okuyoruz.
        var firstRow = used.FirstRow().RowNumber();
        var lastRow = used.LastRow().RowNumber();
        var firstCol = used.FirstColumn().ColumnNumber();
        var lastCol = used.LastColumn().ColumnNumber();
        var width = lastCol - firstCol + 1;

        var headers = new string[width];
        for (var col = firstCol; col <= lastCol; col++)
            headers[col - firstCol] = sheet.Cell(firstRow, col).GetString();

        var rows = new List<string[]>();
        for (var r = firstRow + 1; r <= lastRow; r++)
        {
            var cells = new string[width];
            for (var col = firstCol; col <= lastCol; col++)
                cells[col - firstCol] = CellText(sheet.Cell(r, col));
            rows.Add(cells);
        }

        return Task.FromResult(new ParsedTable(headers, rows));
    }

    // Excel hücresi tipini KENDİ bilir. Gerçek bir tarih hücresini `GetString()` ile
    // okumak o bilgiyi atıp bölgesel görüntü metnini ("27.07.2026") geri tahmin etmeye
    // zorlar. Tarih ve sayıları makine biçiminde yazarak tahmini tümden atlıyoruz.
    // (CSV'de böyle bir imkân yok; orada karar ValueFormats'ta veriliyor.)
    private static string CellText(IXLCell cell) => cell.DataType switch
    {
        XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture),
        XLDataType.Number => cell.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => cell.GetString(),
    };

    public IReadOnlyList<ColumnSchema> DetectSchema(ParsedTable table)
    {
        var columns = new List<ColumnSchema>(table.Headers.Count);

        for (var c = 0; c < table.Headers.Count; c++)
        {
            // O kolondaki boş olmayan değerleri topla (boşluklar tip kararını bozmasın).
            var values = table.Rows
                .Where(r => c < r.Length && !string.IsNullOrWhiteSpace(r[c]))
                .Select(r => r[c])
                .ToList();

            columns.Add(new ColumnSchema(table.Headers[c], ValueFormats.Detect(values).Type));
        }

        return columns;
    }

    // Bir kolonun dosyadaki boş olmayan değerleri (tip/biçim kararı bunlara bakar).
    private static List<string> ColumnValues(ParsedTable table, int index) => table.Rows
        .Where(r => index < r.Length && !string.IsNullOrWhiteSpace(r[index]))
        .Select(r => r[index])
        .ToList();

    // Ham satırları kayıtlı şemaya göre doğrular. Kolonlar ADA göre eşlenir (pozisyona
    // göre değil) — dosyadaki kolon sırası farklı olsa bile doğru hücre bulunur. Tek bir
    // hücre tipine uymazsa o SATIR elenir ve hata listesine eklenir; kalan hücreler yine
    // kontrol edilir (aynı satırın birden çok hatası raporlanabilir).
    public RowValidationResult ValidateRows(ParsedTable table, IReadOnlyList<ColumnSchema> schema)
    {
        // Kolon adı → dosyadaki kolon index'i.
        var headerIndex = new Dictionary<string, int>(table.Headers.Count);
        for (var i = 0; i < table.Headers.Count; i++)
            headerIndex[table.Headers[i]] = i;

        // Biçim kararı kolon başına ve BİR KEZ verilir — algılamadaki kuralın aynısı.
        // Hücre hücre karar verilseydi aynı kolondaki "03.04.2026" ile "13.04.2026"
        // farklı yorumlanabilirdi.
        var formats = new Dictionary<string, ColumnFormat>(schema.Count);
        foreach (var col in schema)
        {
            var values = headerIndex.TryGetValue(col.Name, out var i)
                ? ColumnValues(table, i)
                : new List<string>();
            formats[col.Name] = ValueFormats.For(col.Type, values);
        }

        var validRows = new List<Dictionary<string, object?>>(table.Rows.Count);
        var errors = new List<RowError>();

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var raw = table.Rows[r];
            var rowNumber = r + 1;          // 1 tabanlı veri satırı numarası (başlık hariç)
            var data = new Dictionary<string, object?>(schema.Count);
            var rowOk = true;

            foreach (var col in schema)
            {
                // Başlık kümesi controller'da doğrulandığından ad normalde bulunur; yine de
                // güvenli tarafta kal: yoksa hücreyi boş say.
                var cell = headerIndex.TryGetValue(col.Name, out var idx) && idx < raw.Length
                    ? raw[idx]
                    : null;

                if (string.IsNullOrWhiteSpace(cell))
                {
                    data[col.Name] = null;   // boş hücreye izin ver
                    continue;
                }

                if (TryConvert(cell, formats[col.Name], out var value))
                {
                    data[col.Name] = value;
                }
                else
                {
                    errors.Add(new RowError(rowNumber, col.Name, cell, col.Type));
                    rowOk = false;
                }
            }

            if (rowOk) validRows.Add(data);
        }

        return new RowValidationResult(validRows, errors);
    }

    // Hücreyi kolonun biçimine göre dönüştürür. number → decimal, date → DateTime,
    // text → string. Uymazsa false (satır elenir). Biçim kararı ValueFormats'tan gelir,
    // yani tip algılamayla birebir aynı kuraldır.
    private static bool TryConvert(string cell, ColumnFormat format, out object? value)
    {
        switch (format.Type)
        {
            case "number":
                if (ValueFormats.TryParseNumber(cell, format.Culture, out var d))
                {
                    value = d;
                    return true;
                }
                break;
            case "date":
                if (ValueFormats.TryParseDate(cell, format.DateFormat, out var dt))
                {
                    value = dt;
                    return true;
                }
                break;
            default: // "text" — her string geçerli
                value = cell;
                return true;
        }

        value = null;
        return false;
    }
}
