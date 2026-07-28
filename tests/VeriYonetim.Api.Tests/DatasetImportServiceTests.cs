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

    // ---- Satır validasyonu (ValidateRows) ----

    private static ParsedTable Table(string[] headers, params string[][] rows) =>
        new(headers, rows.ToList());

    [Fact]
    public void ValidateRows_AllValid_ConvertsTypesAndNoErrors()
    {
        var table = Table(new[] { "ad", "yas" }, new[] { "Ali", "30" }, new[] { "Ayse", "25" });
        var schema = new[] { new ColumnSchema("ad", "text"), new ColumnSchema("yas", "number") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.ValidRows.Count);
        Assert.Equal("Ali", result.ValidRows[0]["ad"]);
        Assert.Equal(30m, result.ValidRows[0]["yas"]);   // string "30" → decimal
    }

    [Fact]
    public void ValidateRows_BadNumberCell_SkipsRowAndReportsError()
    {
        var table = Table(new[] { "ad", "yas" }, new[] { "Ali", "30" }, new[] { "Ayse", "abc" });
        var schema = new[] { new ColumnSchema("ad", "text"), new ColumnSchema("yas", "number") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Single(result.ValidRows);                 // sadece geçerli satır kaldı
        var err = Assert.Single(result.Errors);
        Assert.Equal(2, err.Row);                         // 1 tabanlı, başlık sayılmaz
        Assert.Equal("yas", err.Column);
        Assert.Equal("abc", err.Value);
        Assert.Equal("number", err.ExpectedType);
    }

    [Fact]
    public void ValidateRows_EmptyCell_StoredAsNull_RowStaysValid()
    {
        var table = Table(new[] { "ad", "yas" }, new[] { "Ali", "" });
        var schema = new[] { new ColumnSchema("ad", "text"), new ColumnSchema("yas", "number") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Empty(result.Errors);
        Assert.Single(result.ValidRows);
        Assert.Null(result.ValidRows[0]["yas"]);          // boş hücre → null, hata değil
    }

    [Fact]
    public void ValidateRows_ColumnsInDifferentOrder_MappedByName()
    {
        // Dosyada kolonlar ters sırada; eşleme ADA göre yapıldığından doğru değer bulunur.
        var table = Table(new[] { "yas", "ad" }, new[] { "30", "Ali" });
        var schema = new[] { new ColumnSchema("ad", "text"), new ColumnSchema("yas", "number") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Equal("Ali", result.ValidRows[0]["ad"]);
        Assert.Equal(30m, result.ValidRows[0]["yas"]);
    }

    [Fact]
    public void ValidateRows_DateColumn_ConvertsToDateTime()
    {
        var table = Table(new[] { "tarih" }, new[] { "2026-01-15" });
        var schema = new[] { new ColumnSchema("tarih", "date") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Equal(new DateTime(2026, 1, 15), result.ValidRows[0]["tarih"]);
    }

    [Fact]
    public void ValidateRows_MultipleBadCellsInOneRow_ReportedSeparately()
    {
        var table = Table(new[] { "yas", "puan" }, new[] { "x", "y" });
        var schema = new[] { new ColumnSchema("yas", "number"), new ColumnSchema("puan", "number") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Empty(result.ValidRows);                   // satır tümüyle elendi
        Assert.Equal(2, result.Errors.Count);             // iki hücre de raporlandı
    }

    // ---- Ayraç sezme ----
    // Türkçe Windows Excel "CSV olarak kaydet" dediğinde ";" yazar. Sezilmezse dosya
    // TEK kolon olarak okunur, üstelik hata da vermez: veri sessizce kullanılamaz olur.

    [Fact]
    public async Task ParseCsv_SemicolonDelimited_SplitsColumns()
    {
        var table = await _sut.ParseCsvAsync(
            ToStream("musteri;tarih;tutar\nAhmet;27.07.2026;1.500,50"));

        Assert.Equal(new[] { "musteri", "tarih", "tutar" }, table.Headers);
        Assert.Equal(new[] { "Ahmet", "27.07.2026", "1.500,50" }, table.Rows[0]);
    }

    [Fact]
    public async Task ParseCsv_TabDelimited_SplitsColumns()
    {
        var table = await _sut.ParseCsvAsync(ToStream("ad\tyas\nAli\t30"));

        Assert.Equal(new[] { "ad", "yas" }, table.Headers);
        Assert.Equal(new[] { "Ali", "30" }, table.Rows[0]);
    }

    [Fact]
    public async Task ParseCsv_CommaStillDefault_WhenNoOtherDelimiter()
    {
        // Virgül ayraçlı dosyada davranış değişmemeli (gerileme koruması).
        var table = await _sut.ParseCsvAsync(ToStream("ad,yas\nAli,30"));

        Assert.Equal(new[] { "ad", "yas" }, table.Headers);
        Assert.Equal(new[] { "Ali", "30" }, table.Rows[0]);
    }

    // ---- Türkçe biçimli sayı ve tarih ----

    [Fact]
    public void DetectSchema_TurkishDecimal_IsNumber()
    {
        var schema = _sut.DetectSchema(OneColumn("tutar", "1.500,50", "2.750,00", "980,25"));
        Assert.Equal("number", schema[0].Type);
    }

    [Fact]
    public void DetectSchema_TurkishDate_IsDate()
    {
        var schema = _sut.DetectSchema(OneColumn("tarih", "27.07.2026", "28.07.2026"));
        Assert.Equal("date", schema[0].Type);
    }

    [Fact]
    public void DetectSchema_SlashDate_IsDate()
    {
        var schema = _sut.DetectSchema(OneColumn("tarih", "27/07/2026", "01/12/2026"));
        Assert.Equal("date", schema[0].Type);
    }

    [Fact]
    public void ValidateRows_TurkishDecimal_ConvertsToCorrectValue()
    {
        var table = Table(new[] { "tutar" }, new[] { "1.500,50" });
        var schema = new[] { new ColumnSchema("tutar", "number") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Equal(1500.50m, result.ValidRows[0]["tutar"]);
    }

    [Fact]
    public void ValidateRows_TurkishDate_ConvertsDayBeforeMonth()
    {
        // 27.07 → 27 Temmuz. Ay-gün sırasıyla okunsaydı 27. ay diye elenirdi.
        var table = Table(new[] { "tarih" }, new[] { "27.07.2026" });
        var schema = new[] { new ColumnSchema("tarih", "date") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Equal(new DateTime(2026, 7, 27), result.ValidRows[0]["tarih"]);
    }

    [Fact]
    public void ValidateRows_AmbiguousDate_ReadAsDayFirst()
    {
        // "03.04.2026" tek başına belirsiz; ürün Türkçe olduğundan gün-ay sırası esas.
        var table = Table(new[] { "tarih" }, new[] { "03.04.2026" });
        var schema = new[] { new ColumnSchema("tarih", "date") };

        var result = _sut.ValidateRows(table, schema);

        Assert.Equal(new DateTime(2026, 4, 3), result.ValidRows[0]["tarih"]);
    }

    // ---- "1.500" tuzağı ----
    // Bu değer iki türlü okunabilir: 1,5 (nokta ondalık) ya da 1500 (Türkçe binlik).
    // Ayırt edici: noktadan sonra TAM üç hane varsa binlik ayıracıdır.

    [Fact]
    public void DetectSchema_DotWithThreeDigits_ReadAsThousands()
    {
        var table = OneColumn("tutar", "1.500", "12.345");
        var schema = _sut.DetectSchema(table);
        Assert.Equal("number", schema[0].Type);

        var result = _sut.ValidateRows(table, new[] { new ColumnSchema("tutar", "number") });

        Assert.Equal(1500m, result.ValidRows[0]["tutar"]);
        Assert.Equal(12345m, result.ValidRows[1]["tutar"]);
    }

    [Fact]
    public void DetectSchema_DotWithTwoDigits_StaysDecimal()
    {
        var table = OneColumn("oran", "1.25", "3.50");
        var result = _sut.ValidateRows(table, new[] { new ColumnSchema("oran", "number") });

        Assert.Equal(1.25m, result.ValidRows[0]["oran"]);
    }

    [Fact]
    public void DetectSchema_MixedTurkishAndPlain_StaysText()
    {
        // Kolon tutarsızsa zorlamak yerine metin kalır — yanlış sayı üretmekten iyidir.
        var schema = _sut.DetectSchema(OneColumn("karisik", "1.500,50", "abc"));
        Assert.Equal("text", schema[0].Type);
    }

    // ---- Excel hücre tipi ----

    [Fact]
    public async Task ParseExcel_RealDateCell_IsDetectedAsDate()
    {
        // Gerçek tarih hücresi bölgesel biçimde görünür ("27.07.2026"), ama hücrenin
        // KENDİ tipi tarihtir. Görüntü metnini tahmin etmek yerine o tip kullanılır.
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sayfa1");
        ws.Cell(1, 1).Value = "tarih";
        ws.Cell(2, 1).Value = new DateTime(2026, 7, 27);
        ws.Cell(3, 1).Value = new DateTime(2026, 8, 1);
        ws.Column(1).Style.DateFormat.Format = "dd.MM.yyyy";

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        var table = await _sut.ParseExcelAsync(ms);
        var schema = _sut.DetectSchema(table);

        Assert.Equal("date", schema[0].Type);

        var result = _sut.ValidateRows(table, schema);
        Assert.Equal(new DateTime(2026, 7, 27), result.ValidRows[0]["tarih"]);
    }
}
