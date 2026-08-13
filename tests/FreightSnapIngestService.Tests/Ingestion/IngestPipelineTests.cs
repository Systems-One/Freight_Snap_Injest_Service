using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Ingestion;
using FreightSnapIngestService.Models;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Stability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Ingestion;

public class FakeFreightScanRepository : IFreightScanRepository
{
    public List<(ScanRecord Scan, string? CsvPath, IReadOnlyList<string> ImagePaths)> Inserted { get; } = new();
    public bool ThrowOnInsert { get; set; }

    public Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
    {
        if (ThrowOnInsert) throw new InvalidOperationException("simulated DB failure");
        Inserted.Add((scan, csvPath, imagePaths));
        return Task.FromResult((long)Inserted.Count);
    }

    public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct) => Task.CompletedTask;
}

public class IngestPipelineTests : IDisposable
{
    private readonly string _root;
    private readonly FreightIngestOptions _options;
    private readonly FakeFreightScanRepository _repository = new();
    private readonly FakeTimeProvider _time = new();

    public IngestPipelineTests()
    {
        _root = Directory.CreateTempSubdirectory("ingest-pipeline-tests-").FullName;
        _options = new FreightIngestOptions
        {
            CsvDirectory = Path.Combine(_root, "csv"),
            ImageDirectory = Path.Combine(_root, "images"),
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            PendingTimeoutMinutes = 30,
        };
        Directory.CreateDirectory(_options.CsvDirectory);
        Directory.CreateDirectory(_options.ImageDirectory);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IngestPipeline CreatePipeline() => new(
        _options,
        new CsvScanParser(),
        new ImageMatcher(),
        new ScanFileNamer(),
        new PendingScanTracker(_time),
        new FileStabilityTracker(),
        _repository,
        NullLogger<IngestPipeline>.Instance);

    private const string ValidCsv =
        "012345678905,20260813091500,12.5,8.0,6.25,625.0,14.2\n";

    private string WriteCsv(string fileName, string content)
    {
        var path = Path.Combine(_options.CsvDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteImage(string fileName)
    {
        var path = Path.Combine(_options.ImageDirectory, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public async Task Csv_is_not_processed_on_the_first_poll_it_appears_stable_check()
    {
        WriteCsv("scan1.csv", ValidCsv);
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(_repository.Inserted);
        Assert.True(File.Exists(Path.Combine(_options.CsvDirectory, "scan1.csv")));
    }

    [Fact]
    public async Task Finalizes_a_stable_csv_with_matching_images()
    {
        WriteCsv("scan1.csv", ValidCsv);
        WriteImage("012345678905_1.jpg");
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None); // first poll: marks stable-check baseline
        await pipeline.ProcessOnceAsync(CancellationToken.None); // second poll: size unchanged, finalizes

        Assert.Single(_repository.Inserted);
        Assert.Equal("012345678905", _repository.Inserted[0].Scan.Barcode);
        Assert.Single(_repository.Inserted[0].ImagePaths);

        var processedFolder = Path.Combine(_options.ProcessedDirectory, "012345678905_20260813091500");
        Assert.True(File.Exists(Path.Combine(processedFolder, "012345678905_20260813091500.csv")));
        Assert.True(File.Exists(Path.Combine(processedFolder, "012345678905_20260813091500_1.jpg")));
        Assert.False(File.Exists(Path.Combine(_options.CsvDirectory, "scan1.csv")));
        Assert.False(File.Exists(Path.Combine(_options.ImageDirectory, "012345678905_1.jpg")));
    }

    [Fact]
    public async Task Leaves_a_stable_csv_pending_when_no_images_match_yet_and_timeout_has_not_elapsed()
    {
        WriteCsv("scan1.csv", ValidCsv);
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);
        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(_repository.Inserted);
        Assert.True(File.Exists(Path.Combine(_options.CsvDirectory, "scan1.csv")));
    }

    [Fact]
    public async Task Finalizes_without_images_once_the_pending_timeout_elapses()
    {
        WriteCsv("scan1.csv", ValidCsv);
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);
        await pipeline.ProcessOnceAsync(CancellationToken.None); // now pending, no images
        _time.Advance(TimeSpan.FromMinutes(31));
        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Single(_repository.Inserted);
        Assert.Empty(_repository.Inserted[0].ImagePaths);
        var processedFolder = Path.Combine(_options.ProcessedDirectory, "012345678905_20260813091500");
        Assert.True(File.Exists(Path.Combine(processedFolder, "012345678905_20260813091500.csv")));
    }

    [Fact]
    public async Task Moves_a_malformed_csv_to_the_error_directory_immediately()
    {
        WriteCsv("bad.csv", "only,two\n");
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);
        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(_repository.Inserted);
        Assert.True(File.Exists(Path.Combine(_options.ErrorDirectory, "bad.csv")));
        Assert.False(File.Exists(Path.Combine(_options.CsvDirectory, "bad.csv")));
    }

    [Fact]
    public async Task Restores_the_original_files_when_the_database_insert_fails()
    {
        WriteCsv("scan1.csv", ValidCsv);
        WriteImage("012345678905_1.jpg");
        _repository.ThrowOnInsert = true;
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);
        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(_repository.Inserted);
        Assert.True(File.Exists(Path.Combine(_options.CsvDirectory, "scan1.csv")));
        Assert.True(File.Exists(Path.Combine(_options.ImageDirectory, "012345678905_1.jpg")));
        Assert.False(Directory.Exists(_options.ProcessedDirectory) &&
            Directory.EnumerateFileSystemEntries(_options.ProcessedDirectory).Any());
    }
}
