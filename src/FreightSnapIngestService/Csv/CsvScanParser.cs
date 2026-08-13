using System.Globalization;
using FreightSnapIngestService.Models;

namespace FreightSnapIngestService.Csv;

public interface ICsvScanParser
{
    ScanRecord Parse(string csvContent);
}

public class CsvScanParser : ICsvScanParser
{
    private static readonly string[] RequiredColumns =
        { "Barcode", "DateTime", "Length", "Width", "Height", "Volume", "Weight" };

    public ScanRecord Parse(string csvContent)
    {
        var lines = csvContent
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        if (lines.Count == 0 || string.IsNullOrWhiteSpace(lines[0]))
            throw new CsvParseException("CSV has no header row.");

        var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Length; i++)
            columnIndex[headers[i]] = i;

        foreach (var required in RequiredColumns)
        {
            if (!columnIndex.ContainsKey(required))
                throw new CsvParseException($"CSV is missing required column '{required}'.");
        }

        var dataLine = lines.Skip(1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (dataLine is null)
            throw new CsvParseException("CSV has no data row.");

        var fields = dataLine.Split(',');

        string Field(string column) => fields[columnIndex[column]].Trim();

        decimal? ParseDecimal(string column)
        {
            var raw = Field(column);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new CsvParseException($"Column '{column}' value '{raw}' is not a valid number.");
            return value;
        }

        var barcode = Field("Barcode");
        if (string.IsNullOrWhiteSpace(barcode))
            throw new CsvParseException("Barcode column is empty.");

        var dateTimeRaw = Field("DateTime");
        if (!DateTime.TryParse(dateTimeRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var scanDateTime))
            throw new CsvParseException($"Column 'DateTime' value '{dateTimeRaw}' is not a valid date/time.");

        return new ScanRecord(
            barcode,
            scanDateTime,
            ParseDecimal("Length"),
            ParseDecimal("Width"),
            ParseDecimal("Height"),
            ParseDecimal("Volume"),
            ParseDecimal("Weight"));
    }
}
