namespace FreightSnapIngestService.Naming;

public class ScanFileNamer
{
    private const string TimestampFormat = "yyyyMMddHHmmss";

    public string BuildProcessedFolderName(string barcode, DateTime scanDateTime) =>
        $"{barcode}_{scanDateTime.ToString(TimestampFormat)}";

    public string BuildCsvFileName(string barcode, DateTime scanDateTime) =>
        $"{BuildProcessedFolderName(barcode, scanDateTime)}.csv";

    public string BuildImageFileName(string barcode, DateTime scanDateTime, int sequenceNumber, string originalExtension) =>
        $"{BuildProcessedFolderName(barcode, scanDateTime)}_{sequenceNumber}{originalExtension}";
}
