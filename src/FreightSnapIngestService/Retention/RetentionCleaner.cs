using FreightSnapIngestService.Db;
using FreightSnapIngestService.Options;

namespace FreightSnapIngestService.Retention;

public class RetentionCleaner(
    FreightIngestOptions options,
    IFreightScanRepository repository,
    TimeProvider timeProvider)
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

                Directory.Delete(folder, recursive: true);
                await repository.ClearPathsUnderFolderAsync(folder, ct);
            }
        }

        if (Directory.Exists(options.ErrorDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(options.ErrorDirectory))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;

                File.Delete(file);
            }
        }
    }
}
