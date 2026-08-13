using FreightSnapIngestService.Csv;
using Xunit;

namespace FreightSnapIngestService.Tests.Csv;

public class CsvScanParserTests
{
    private readonly CsvScanParser _parser = new();

    [Fact]
    public void Parses_a_valid_row_into_a_ScanRecord()
    {
        var csv = "Barcode,DateTime,Length,Width,Height,Volume,Weight\n" +
                   "012345678905,2026-08-13T09:15:00,12.5,8.0,6.25,625.0,14.2\n";

        var result = _parser.Parse(csv);

        Assert.Equal("012345678905", result.Barcode);
        Assert.Equal(new DateTime(2026, 8, 13, 9, 15, 0), result.ScanDateTime);
        Assert.Equal(12.5m, result.Length);
        Assert.Equal(8.0m, result.Width);
        Assert.Equal(6.25m, result.Height);
        Assert.Equal(625.0m, result.Volume);
        Assert.Equal(14.2m, result.Weight);
    }

    [Fact]
    public void Parses_columns_in_any_order()
    {
        var csv = "Weight,Barcode,DateTime,Height,Volume,Width,Length\n" +
                   "14.2,012345678905,2026-08-13T09:15:00,6.25,625.0,8.0,12.5\n";

        var result = _parser.Parse(csv);

        Assert.Equal("012345678905", result.Barcode);
        Assert.Equal(14.2m, result.Weight);
    }

    [Fact]
    public void Throws_CsvParseException_when_a_required_column_is_missing()
    {
        var csv = "Barcode,DateTime,Length,Width,Height,Volume\n" +
                   "012345678905,2026-08-13T09:15:00,12.5,8.0,6.25,625.0\n";

        var ex = Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
        Assert.Contains("Weight", ex.Message);
    }

    [Fact]
    public void Throws_CsvParseException_when_there_is_no_data_row()
    {
        var csv = "Barcode,DateTime,Length,Width,Height,Volume,Weight\n";

        Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
    }

    [Fact]
    public void Throws_CsvParseException_when_a_numeric_field_is_not_a_number()
    {
        var csv = "Barcode,DateTime,Length,Width,Height,Volume,Weight\n" +
                   "012345678905,2026-08-13T09:15:00,not-a-number,8.0,6.25,625.0,14.2\n";

        var ex = Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
        Assert.Contains("Length", ex.Message);
    }

    [Fact]
    public void Throws_CsvParseException_when_the_barcode_is_empty()
    {
        var csv = "Barcode,DateTime,Length,Width,Height,Volume,Weight\n" +
                   ",2026-08-13T09:15:00,12.5,8.0,6.25,625.0,14.2\n";

        Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
    }

    [Fact]
    public void Skips_blank_lines_and_uses_the_first_non_blank_data_row()
    {
        var csv = "Barcode,DateTime,Length,Width,Height,Volume,Weight\n" +
                   "\n" +
                   "012345678905,2026-08-13T09:15:00,12.5,8.0,6.25,625.0,14.2\n" +
                   "\n";

        var result = _parser.Parse(csv);

        Assert.Equal("012345678905", result.Barcode);
    }
}
