using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;

namespace VeriYonetim.Api.Services;

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
        using var reader = new StreamReader(stream);
        // CsvParser: düşük seviye okuyucu — her satırı ham string[] olarak verir.
        // Tipli sınıfa map ETMİYORUZ; kolonlar önceden bilinmediğinden dinamik okuyoruz.
        // InvariantCulture: ayraç/format yorumu makineden bağımsız, tutarlı olsun.
        using var parser = new CsvParser(reader, CultureInfo.InvariantCulture);

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
                cells[col - firstCol] = sheet.Cell(r, col).GetString();
            rows.Add(cells);
        }

        return Task.FromResult(new ParsedTable(headers, rows));
    }

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

            columns.Add(new ColumnSchema(table.Headers[c], DetectColumnType(values)));
        }

        return columns;
    }

    private static string DetectColumnType(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return "text";          // hiç değer yok → varsayılan
        if (values.All(IsNumber)) return "number";     // sayıyı tarihten önce dene
        if (values.All(IsDate)) return "date";
        return "text";                                 // karışık/metin → text
    }

    // Kültür varsayımı: nokta ondalık ayıracı (InvariantCulture). Türkçe "1.500,50"
    // biçimi değil, "1500.50" beklenir — CSV export'larının yaygın davranışı.
    private static bool IsNumber(string v) =>
        decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

    private static bool IsDate(string v) =>
        DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

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

                if (TryConvert(cell, col.Type, out var value))
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

    // Hücreyi şema tipine dönüştürür. number → decimal, date → DateTime, text → string.
    // Uymazsa false (satır elenir). Kültür: InvariantCulture (tip algılamayla aynı kural).
    private static bool TryConvert(string cell, string type, out object? value)
    {
        switch (type)
        {
            case "number":
                if (decimal.TryParse(cell, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                {
                    value = d;
                    return true;
                }
                break;
            case "date":
                if (DateTime.TryParse(cell, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
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
