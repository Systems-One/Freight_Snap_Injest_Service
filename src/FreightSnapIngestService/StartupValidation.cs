using FreightSnapIngestService.Options;

namespace FreightSnapIngestService;

/// <summary>
/// Logs the resolved <see cref="FreightIngestOptions"/> once at startup and flags any
/// configured directories that don't currently exist, so a misconfiguration (typo,
/// wrong path, a "FreightIngest" section that failed to bind) is loud in the log from
/// the very first run instead of silently causing the pipeline to never ingest anything.
/// </summary>
public static class StartupValidation
{
    public static void LogConfiguration(FreightIngestOptions options, ILogger logger)
    {
        logger.LogInformation(
            "Resolved FreightIngest configuration: CsvDirectory={CsvDirectory}, ImageDirectory={ImageDirectory}, " +
            "ProcessedDirectory={ProcessedDirectory}, ErrorDirectory={ErrorDirectory}, PollIntervalSeconds={PollIntervalSeconds}, " +
            "PendingTimeoutMinutes={PendingTimeoutMinutes}, RetentionDays={RetentionDays}, ConnectionString={ConnectionString}",
            options.CsvDirectory, options.ImageDirectory, options.ProcessedDirectory, options.ErrorDirectory,
            options.PollIntervalSeconds, options.PendingTimeoutMinutes, options.RetentionDays,
            string.IsNullOrWhiteSpace(options.ConnectionString) ? "[NOT CONFIGURED]" : "[REDACTED]");

        foreach (var missing in GetMissingDirectories(options))
        {
            logger.LogWarning(
                "Configured directory {DirectoryName}={DirectoryPath} does not currently exist on disk.",
                missing.Name, missing.Path);
        }
    }

    public static IReadOnlyList<(string Name, string Path)> GetMissingDirectories(FreightIngestOptions options)
    {
        var candidates = new (string Name, string Path)[]
        {
            (nameof(options.CsvDirectory), options.CsvDirectory),
            (nameof(options.ImageDirectory), options.ImageDirectory),
            (nameof(options.ProcessedDirectory), options.ProcessedDirectory),
            (nameof(options.ErrorDirectory), options.ErrorDirectory),
        };

        return candidates.Where(c => !Directory.Exists(c.Path)).ToList();
    }
}
