namespace FreightSnapIngestService.Models;

public record ScanRecord(
    string Barcode,
    DateTime ScanDateTime,
    decimal? Length,
    decimal? Width,
    decimal? Height,
    decimal? Volume,
    decimal? Weight);
