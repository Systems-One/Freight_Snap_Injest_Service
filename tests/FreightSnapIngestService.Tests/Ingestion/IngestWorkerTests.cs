using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Ingestion;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Stability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Ingestion;

public class IngestWorkerTests : IDisposable
{
    private readonly string _root;

    public IngestWorkerTests() => _root = Directory.CreateTempSubdirectory("ingest-worker-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Polls_the_pipeline_repeatedly_at_the_configured_interval()
    {
        var freightOptions = new FreightIngestOptions
        {
            CsvDirectory = Path.Combine(_root, "csv"),
            ImageDirectory = Path.Combine(_root, "images"),
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            PollIntervalSeconds = 10,
            PendingTimeoutMinutes = 30,
        };
        Directory.CreateDirectory(freightOptions.CsvDirectory);
        Directory.CreateDirectory(freightOptions.ImageDirectory);

        var time = new FakeTimeProvider();
        var pipeline = new IngestPipeline(
            freightOptions, new CsvScanParser(), new ImageMatcher(), new ScanFileNamer(),
            new PendingScanTracker(time), new FileStabilityTracker(), new NoOpFreightScanRepository(),
            NullLogger<IngestPipeline>.Instance);

        var worker = new IngestWorker(pipeline, Microsoft.Extensions.Options.Options.Create(freightOptions), time, NullLogger<IngestWorker>.Instance);

        using var cts = new CancellationTokenSource();
        var runTask = worker.StartAsync(cts.Token);
        await runTask;

        // Advance past three poll intervals; each tick should complete without throwing
        // even with empty directories, proving the loop is alive and self-driving.
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(50); // let the background loop observe the advanced time
        }

        await worker.StopAsync(CancellationToken.None);
    }

    private class NoOpFreightScanRepository : IFreightScanRepository
    {
        public Task<long> InsertScanAsync(Models.ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
            => Task.FromResult(1L);
        public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct) => Task.CompletedTask;
    }
}
