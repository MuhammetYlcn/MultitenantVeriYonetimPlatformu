using System.Text;
using ClosedXML.Excel;
using VeriYonetim.Api.Services;

namespace VeriYonetim.Api.Tests;

// Saf (DB/HTTP'siz) birim testler: servisi doğrudan new'leyip çağırıyoruz.
// Entegrasyon testlerinden farkı — ne WebApplicationFactory ne veritabanı gerekir.
public class DatasetImportServiceTests
{
    private readonly DatasetImportService _sut = new();

    private static Stream ToStream(string content) =>
        new MemoryStream(Encoding.UTF8.GetBytes(content));

    // ---- CSV parse ----

    [Fact]
    public async Task ParseCsv_SeparatesHeaderAndRows()
    {
        var table = await _sut.ParseCsvAsync(ToStream("ad,yas\nAli,30\nAyse,25"));

        Assert.Equal(new[] { "ad", "yas" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);              // başlık satır sayısına dahil değil
        Assert.Equal(new[] { "Ali", "30" }, table.Rows[0]);
    }

    [Fact]
    public async Task ParseCsv_HandlesQuotedComma()
    {
        // Tırnak içindeki virgül ayraç değil, hücre içeriğidir — CsvHelper'ın işi.
        var table = await _sut.ParseCsvAsync(ToStream("sehir,nufus\n\"Ankara, TR\",5000000"));

        Assert.Single(table.Rows);
        Assert.Equal("Ankara, TR", table.Rows[0][0]);
        Assert.Equal("5000000", table.Rows[0][1]);
    }

    [Fact]
    public async Task ParseCsv_EmptyStream_Throws()
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => _sut.ParseCsvAsync(ToStream("")));
    }

    // ---- Tip algılama ----

    // Tek kolonluk ParsedTable üretir (değerler o kolonun satırları).
    private static ParsedTable OneColumn(string header, params string[] values)
    {
        var rows = values.Select(v => new[] { v }).ToList();
        return new ParsedTable(new[] { header }, rows);
    }

    [Fact]
    public void DetectSchema_AllNumbers_IsNumber()
    {
        var schema = _sut.DetectSchema(OneColumn("miktar", "10", "20.5", "30"));
        Assert.Equal("number", schema[0].Type);
    }

    [Fact]
    public void DetectSchema_AllDates_IsDate()
    {
        var schema = _sut.DetectSchema(OneColumn("tarih", "2026-01-15", "2026-02-20"));
        Assert.Equal("date", schema[0].Type);
    }

    [Fact]
    public void DetectSchema_TextValues_IsText()
    {
        var schema = _sut.DetectSchema(OneColumn("ad", "Ali", "Ayse"));
        Assert.Equal("text", schema[0].Type);
    }

    [Fact]
    public void DetectSchema_MixedNumberAndText_IsText()
    {
        // Tek bozuk değer bile "hepsi sayı" kuralını bozar → text (güvenli taraf).
        var schema = _sut.DetectSchema(OneColumn("kod", "10", "yok", "30"));
        Assert.Equal("text", schema[0].Type);
    }

    [Fact]
    public void DetectSchema_IgnoresEmptyValues()
    {
        // Ortadaki boş değer atlanır; kalanların hepsi sayı → number.
        var schema = _sut.DetectSchema(OneColumn("olcum", "3.5", "", "7.2"));
        Assert.Equal("number", schema[0].Type);
    }

    [Fact]
    public void DetectSchema_AllEmpty_IsText()
    {
        // Hiç değer yok (varsayılan) → text.
        var schema = _sut.DetectSchema(OneColumn("bos", "", "  "));
        Assert.Equal("text", schema[0].Type);
    }

    [Fact]
    public void DetectSchema_MultipleColumns_EachDetectedIndependently()
    {
        var table = new ParsedTable(
            new[] { "ad", "yas", "tarih" },
            new List<string[]>
            {
                new[] { "Ali", "30", "2026-01-15" },
                new[] { "Ayse", "25", "2026-02-20" }
            });

        var schema = _sut.DetectSchema(table);

        Assert.Equal("text", schema[0].Type);
        Assert.Equal("number", schema[1].Type);
        Assert.Equal("date", schema[2].Type);
    }

    // ---- Excel parse ----

    [Fact]
    public async Task ParseExcel_ReadsCellsIntoTable()
    {
        // Testin kendisi bir xlsx üretip yine kendisi okur (bellek içi).
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "ad";
        ws.Cell(1, 2).Value = "yas";
        ws.Cell(2, 1).Value = "Ali";
        ws.Cell(2, 2).Value = 30;
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var table = await _sut.ParseExcelAsync(ms);

        Assert.Equal(new[] { "ad", "yas" }, table.Headers);
        Assert.Single(table.Rows);
        Assert.Equal("Ali", table.Rows[0][0]);
        Assert.Equal("30", table.Rows[0][1]);
    }
}
