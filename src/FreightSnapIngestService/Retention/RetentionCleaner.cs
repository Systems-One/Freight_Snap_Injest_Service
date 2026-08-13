using FreightSnapIngestService.Db;
using FreightSnapIngestService.Options;

namespace FreightSnapIngestService.Retention;

public class RetentionCleaner(
    FreightIngestOptions options,
    IFreightScanRepository repository,
    TimeProvider timeProvider,
    ILogger<RetentionCleaner> logger)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(options.RetentionDays);

        if (Directory.Exists(options.ProcessedDirectory))
        {
            foreach (var folder in Directory.EnumerateDirectories(options.ProcessedDirectory))
            {
                if (Directory.GetLastWriteTimeUtc(folder) >= cutoff)
                    continue;

                try
                {
                    // Clear the DB paths BEFORE deleting the folder from disk. If the DB call
                    // throws (e.g. a transient SQL issue), the folder is left untouched and
                    // still expired, so it is simply retried on the next hourly pass —
                    // ClearPathsUnderFolderAsync is idempotent, so re-clearing already-cleared
                    // paths is a harmless no-op. Deleting the folder first would instead leave
                    // DB rows pointing at files that no longer exist, forever, since a deleted
                    // folder can never be re-discovered by EnumerateDirectories.
                    await repository.ClearPathsUnderFolderAsync(folder, ct);
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex)
                {
                    // One bad folder (locked file, permission issue, transient DB failure)
                    // must not abort the rest of the retention pass.
                    logger.LogError(ex, "Failed to clean up expired processed folder {Folder}; it will be retried next pass.", folder);
                }
            }
        }

        if (Directory.Exists(options.ErrorDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(options.ErrorDirectory))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to delete expired error file {File}; it will be retried next pass.", file);
                }
            }
        }
    }
}
