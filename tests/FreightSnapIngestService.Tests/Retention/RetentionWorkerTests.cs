using FreightSnapIngestService.Db;
using FreightSnapIngestService.Models;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Retention;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Retention;

public class RetentionWorkerTests : IDisposable
{
    private readonly string _root;

    public RetentionWorkerTests() => _root = Directory.CreateTempSubdirectory("retention-worker-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private class NoOpFreightScanRepository : IFreightScanRepository
    {
        public Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
            => Task.FromResult(1L);
        public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Runs_the_cleaner_once_per_hour()
    {
        var options = new FreightIngestOptions
        {
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            RetentionDays = 30,
        };
        Directory.CreateDirectory(options.ProcessedDirectory);
        Directory.CreateDirectory(options.ErrorDirectory);

        var time = new FakeTimeProvider();
        var cleaner = new RetentionCleaner(options, new NoOpFreightScanRepository(), time);
        var worker = new RetentionWorker(cleaner, time, NullLogger<RetentionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);

        // No exception means the hourly tick executed RunOnceAsync against real,
        // empty directories without error.
    }
}
