namespace FreightSnapIngestService.Csv;

public class CsvParseException : Exception
{
    public CsvParseException(string message) : base(message) { }
}
