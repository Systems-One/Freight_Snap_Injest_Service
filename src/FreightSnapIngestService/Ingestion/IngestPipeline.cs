using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Stability;

namespace FreightSnapIngestService.Ingestion;

public class IngestPipeline(
    FreightIngestOptions options,
    ICsvScanParser csvParser,
    IImageMatcher imageMatcher,
    ScanFileNamer fileNamer,
    PendingScanTracker pendingTracker,
    FileStabilityTracker stabilityTracker,
    IFreightScanRepository repository,
    ILogger<IngestPipeline> logger)
{
    // FileStabilityTracker.IsStable is a self-clearing, two-poll gate: once it reports
    // true for a path it forgets that path, so calling it again on a later poll treats
    // the file as brand-new (requiring another two polls) instead of remembering it
    // already proved stable. A CSV that is left pending (no matching images yet) keeps
    // being polled every cycle, so we remember here which paths have already cleared the
    // gate and skip re-checking them — otherwise a pending file could never reach its
    // timeout because IsStable would keep resetting on every poll after the first pending
    // observation.
    private readonly HashSet<string> _stableCsvPaths = new();

    public async Task ProcessOnceAsync(CancellationToken ct)
    {
        if (!Directory.Exists(options.CsvDirectory))
            return;

        foreach (var csvPath in Directory.EnumerateFiles(options.CsvDirectory, "*.csv"))
        {
            try
            {
                await ProcessCsvAsync(csvPath, ct);
            }
            catch (Exception ex)
            {
                // A single bad file must not stop the rest of the batch; it is retried
                // on the next poll cycle since it was never moved out of CsvDirectory.
                logger.LogError(ex, "Failed to process {CsvPath}; it will be retried next poll cycle.", csvPath);
            }
        }
    }

    private async Task ProcessCsvAsync(string csvPath, CancellationToken ct)
    {
        if (!_stableCsvPaths.Contains(csvPath))
        {
            var currentSize = new FileInfo(csvPath).Length;
            if (!stabilityTracker.IsStable(csvPath, currentSize))
                return;

            _stableCsvPaths.Add(csvPath);
        }

        Models.ScanRecord scan;
        try
        {
            scan = csvParser.Parse(await File.ReadAllTextAsync(csvPath, ct));
        }
        catch (CsvParseException ex)
        {
            logger.LogWarning(ex, "{CsvPath} failed to parse and is being moved to the error directory.", csvPath);
            Directory.CreateDirectory(options.ErrorDirectory);
            File.Move(csvPath, Path.Combine(options.ErrorDirectory, Path.GetFileName(csvPath)), overwrite: true);
            _stableCsvPaths.Remove(csvPath);
            return;
        }

        var matchedImages = imageMatcher.FindMatchingImages(options.ImageDirectory, scan.Barcode);

        if (matchedImages.Count == 0)
        {
            pendingTracker.TrackFirstSeen(csvPath);
            if (!pendingTracker.HasTimedOut(csvPath, TimeSpan.FromMinutes(options.PendingTimeoutMinutes)))
            {
                logger.LogInformation(
                    "Barcode {Barcode} ({CsvPath}) has no matching images yet; leaving pending.",
                    scan.Barcode, csvPath);
                return;
            }

            logger.LogWarning(
                "Barcode {Barcode} ({CsvPath}) timed out waiting for images after {TimeoutMinutes} minutes; finalizing without images.",
                scan.Barcode, csvPath, options.PendingTimeoutMinutes);
        }

        await FinalizeAsync(csvPath, scan, matchedImages, ct);
        pendingTracker.Remove(csvPath);
        _stableCsvPaths.Remove(csvPath);
        logger.LogInformation(
            "Finalized barcode {Barcode} ({CsvPath}) with {ImageCount} image(s).",
            scan.Barcode, csvPath, matchedImages.Count);
    }

    private async Task FinalizeAsync(string csvPath, Models.ScanRecord scan, IReadOnlyList<string> matchedImages, CancellationToken ct)
    {
        var folderName = fileNamer.BuildProcessedFolderName(scan.Barcode, scan.ScanDateTime);
        var destinationFolder = Path.Combine(options.ProcessedDirectory, folderName);
        Directory.CreateDirectory(destinationFolder);

        var destinationCsvPath = Path.Combine(destinationFolder, fileNamer.BuildCsvFileName(scan.Barcode, scan.ScanDateTime));
        File.Move(csvPath, destinationCsvPath);

        var destinationImagePaths = new List<string>();
        var movedSourceImagePaths = new List<string>();
        try
        {
            for (var i = 0; i < matchedImages.Count; i++)
            {
                var sourceImagePath = matchedImages[i];
                var extension = Path.GetExtension(sourceImagePath);
                var destinationImagePath = Path.Combine(
                    destinationFolder,
                    fileNamer.BuildImageFileName(scan.Barcode, scan.ScanDateTime, i + 1, extension));

                File.Move(sourceImagePath, destinationImagePath);
                movedSourceImagePaths.Add(sourceImagePath);
                destinationImagePaths.Add(destinationImagePath);
            }

            await repository.InsertScanAsync(scan, destinationCsvPath, destinationImagePaths, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Database insert failed for barcode {Barcode} ({CsvPath}); reverting file moves for retry.",
                scan.Barcode, csvPath);

            File.Move(destinationCsvPath, csvPath);
            for (var i = 0; i < movedSourceImagePaths.Count; i++)
                File.Move(destinationImagePaths[i], movedSourceImagePaths[i]);

            if (!Directory.EnumerateFileSystemEntries(destinationFolder).Any())
                Directory.Delete(destinationFolder);

            throw;
        }
    }
}
