using FreightSnapIngestService.Csv;
using Xunit;

namespace FreightSnapIngestService.Tests.Csv;

public class CsvScanParserTests
{
    private readonly CsvScanParser _parser = new();

    [Fact]
    public void Parses_a_valid_headerless_row_into_a_ScanRecord()
    {
        var csv = "724076,20260804144415,122.0,101.5,128.5,1.591216,216.000\n";

        var result = _parser.Parse(csv);

        Assert.Equal("724076", result.Barcode);
        Assert.Equal(new DateTime(2026, 8, 4, 14, 44, 15), result.ScanDateTime);
        Assert.Equal(122.0m, result.Length);
        Assert.Equal(101.5m, result.Width);
        Assert.Equal(128.5m, result.Height);
        Assert.Equal(1.591216m, result.Volume);
        Assert.Equal(216.000m, result.Weight);
    }

    [Fact]
    public void Accepts_a_barcode_field_with_extra_leading_characters_as_is()
    {
        var csv = "vvhjmfvjhfg724076,20260804144524,122.0,101.5,128.5,1.591216,216.000\n";

        var result = _parser.Parse(csv);

        Assert.Equal("vvhjmfvjhfg724076", result.Barcode);
    }

    [Fact]
    public void Throws_CsvParseException_when_a_field_is_missing()
    {
        var csv = "724076,20260804144415,122.0,101.5,128.5,1.591216\n";

        var ex = Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
        Assert.Contains("6 field", ex.Message);
    }

    [Fact]
    public void Throws_CsvParseException_when_there_is_no_data_row()
    {
        var csv = "\n\n";

        Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
    }

    [Fact]
    public void Throws_CsvParseException_when_a_numeric_field_is_not_a_number()
    {
        var csv = "724076,20260804144415,not-a-number,101.5,128.5,1.591216,216.000\n";

        var ex = Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
        Assert.Contains("Length", ex.Message);
    }

    [Fact]
    public void Throws_CsvParseException_when_the_barcode_field_is_empty()
    {
        var csv = ",20260804144415,122.0,101.5,128.5,1.591216,216.000\n";

        Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
    }

    [Fact]
    public void Throws_CsvParseException_when_the_datetime_is_not_in_the_expected_format()
    {
        var csv = "724076,2026-08-04T14:44:15,122.0,101.5,128.5,1.591216,216.000\n";

        var ex = Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
        Assert.Contains("DateTime", ex.Message);
    }

    [Fact]
    public void Skips_blank_lines_and_uses_the_first_non_blank_data_row()
    {
        var csv = "\n724076,20260804144415,122.0,101.5,128.5,1.591216,216.000\n\n";

        var result = _parser.Parse(csv);

        Assert.Equal("724076", result.Barcode);
    }
}
