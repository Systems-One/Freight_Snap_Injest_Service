using System.Globalization;
using FreightSnapIngestService.Models;

namespace FreightSnapIngestService.Csv;

public interface ICsvScanParser
{
    ScanRecord Parse(string csvContent);
}

public class CsvScanParser : ICsvScanParser
{
    private const int ExpectedFieldCount = 7;
    private const string DateTimeFormat = "yyyyMMddHHmmss";

    public ScanRecord Parse(string csvContent)
    {
        var dataLine = csvContent
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (dataLine is null)
            throw new CsvParseException("CSV has no data row.");

        var fields = dataLine.Split(',').Select(f => f.Trim()).ToArray();
        if (fields.Length != ExpectedFieldCount)
            throw new CsvParseException(
                $"CSV data row has {fields.Length} field(s); expected {ExpectedFieldCount} " +
                "(Barcode, DateTime, Length, Width, Height, Volume, Weight).");

        var barcode = fields[0];
        if (string.IsNullOrWhiteSpace(barcode))
            throw new CsvParseException("Barcode field is empty.");

        var dateTimeRaw = fields[1];
        if (!DateTime.TryParseExact(
                dateTimeRaw, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var scanDateTime))
            throw new CsvParseException($"DateTime field value '{dateTimeRaw}' is not a valid '{DateTimeFormat}' date/time.");

        decimal? ParseDecimal(string fieldName, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new CsvParseException($"{fieldName} field value '{raw}' is not a valid number.");
            return value;
        }

        return new ScanRecord(
            barcode,
            scanDateTime,
            ParseDecimal("Length", fields[2]),
            ParseDecimal("Width", fields[3]),
            ParseDecimal("Height", fields[4]),
            ParseDecimal("Volume", fields[5]),
            ParseDecimal("Weight", fields[6]));
    }
}
