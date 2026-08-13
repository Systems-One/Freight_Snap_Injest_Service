using FreightSnapIngestService.Db;
using FreightSnapIngestService.Models;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Retention;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Retention;

public class FakeFreightScanRepository : IFreightScanRepository
{
    public List<string> ClearedFolderPrefixes { get; } = new();

    public Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
        => Task.FromResult(1L);

    public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct)
    {
        ClearedFolderPrefixes.Add(folderPathPrefix);
        return Task.CompletedTask;
    }
}

public class RetentionCleanerTests : IDisposable
{
    private readonly string _root;
    private readonly FreightIngestOptions _options;
    private readonly FakeFreightScanRepository _repository = new();
    private readonly FakeTimeProvider _time = new();

    public RetentionCleanerTests()
    {
        _root = Directory.CreateTempSubdirectory("retention-cleaner-tests-").FullName;
        _options = new FreightIngestOptions
        {
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            RetentionDays = 30,
        };
        Directory.CreateDirectory(_options.ProcessedDirectory);
        Directory.CreateDirectory(_options.ErrorDirectory);
        _time.SetUtcNow(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateProcessedFolder(string name, DateTime lastWriteTimeUtc)
    {
        var folder = Path.Combine(_options.ProcessedDirectory, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"{name}.csv"), "x");
        Directory.SetLastWriteTimeUtc(folder, lastWriteTimeUtc);
        return folder;
    }

    [Fact]
    public async Task Deletes_processed_folders_older_than_the_retention_period_and_clears_db_paths()
    {
        var oldFolder = CreateProcessedFolder("012345678905_20260601090000", new DateTime(2026, 6, 1));
        var cleaner = new RetentionCleaner(_options, _repository, _time);

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(oldFolder));
        Assert.Contains(oldFolder, _repository.ClearedFolderPrefixes);
    }

    [Fact]
    public async Task Keeps_processed_folders_within_the_retention_period()
    {
        var recentFolder = CreateProcessedFolder("012345678905_20260812090000", new DateTime(2026, 8, 12));
        var cleaner = new RetentionCleaner(_options, _repository, _time);

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.True(Directory.Exists(recentFolder));
        Assert.DoesNotContain(recentFolder, _repository.ClearedFolderPrefixes);
    }

    [Fact]
    public async Task Deletes_expired_files_directly_under_the_error_directory()
    {
        var errorFile = Path.Combine(_options.ErrorDirectory, "bad.csv");
        File.WriteAllText(errorFile, "x");
        File.SetLastWriteTimeUtc(errorFile, new DateTime(2026, 6, 1));
        var cleaner = new RetentionCleaner(_options, _repository, _time);

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(errorFile));
    }

    [Fact]
    public async Task Keeps_recent_files_under_the_error_directory()
    {
        var errorFile = Path.Combine(_options.ErrorDirectory, "bad.csv");
        File.WriteAllText(errorFile, "x");
        File.SetLastWriteTimeUtc(errorFile, new DateTime(2026, 8, 12));
        var cleaner = new RetentionCleaner(_options, _repository, _time);

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(errorFile));
    }
}
