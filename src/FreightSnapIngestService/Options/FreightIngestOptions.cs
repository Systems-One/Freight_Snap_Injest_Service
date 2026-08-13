namespace FreightSnapIngestService.Options;

public class FreightIngestOptions
{
    public const string SectionName = "FreightIngest";

    public string CsvDirectory { get; set; } = "";
    public string ImageDirectory { get; set; } = "";
    public string ProcessedDirectory { get; set; } = "";
    public string ErrorDirectory { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 10;
    public int PendingTimeoutMinutes { get; set; } = 30;
    public int RetentionDays { get; set; } = 30;
    public string ConnectionString { get; set; } = "";
}
