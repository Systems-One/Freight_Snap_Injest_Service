using FreightSnapIngestService.Db;
using FreightSnapIngestService.Models;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Retention;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Retention;

public class FakeFreightScanRepository : IFreightScanRepository
{
    public List<string> ClearedFolderPrefixes { get; } = new();

    /// <summary>
    /// Records, for each folder cleared, whether that folder still existed on disk at the
    /// moment ClearPathsUnderFolderAsync was called for it — used to prove the DB clear
    /// happens BEFORE the disk delete (not after).
    /// </summary>
    public List<(string Folder, bool FolderExistedAtClearTime)> ClearCallsWithDiskState { get; } = new();

    public bool ThrowOnClear { get; set; }
    public Func<string, bool>? ThrowOnClearForFolder { get; set; }

    public Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
        => Task.FromResult(1L);

    public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct)
    {
        ClearCallsWithDiskState.Add((folderPathPrefix, Directory.Exists(folderPathPrefix)));

        if (ThrowOnClear || (ThrowOnClearForFolder?.Invoke(folderPathPrefix) ?? false))
            throw new InvalidOperationException("simulated DB failure");

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

    private RetentionCleaner CreateCleaner() =>
        new(_options, _repository, _time, NullLogger<RetentionCleaner>.Instance);

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
        var cleaner = CreateCleaner();

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(oldFolder));
        Assert.Contains(oldFolder, _repository.ClearedFolderPrefixes);
    }

    [Fact]
    public async Task Keeps_processed_folders_within_the_retention_period()
    {
        var recentFolder = CreateProcessedFolder("012345678905_20260812090000", new DateTime(2026, 8, 12));
        var cleaner = CreateCleaner();

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
        var cleaner = CreateCleaner();

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(errorFile));
    }

    [Fact]
    public async Task Keeps_recent_files_under_the_error_directory()
    {
        var errorFile = Path.Combine(_options.ErrorDirectory, "bad.csv");
        File.WriteAllText(errorFile, "x");
        File.SetLastWriteTimeUtc(errorFile, new DateTime(2026, 8, 12));
        var cleaner = CreateCleaner();

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(errorFile));
    }

    [Fact]
    public async Task Clears_db_paths_before_deleting_the_folder_from_disk()
    {
        var oldFolder = CreateProcessedFolder("012345678905_20260601090000", new DateTime(2026, 6, 1));
        var cleaner = CreateCleaner();

        await cleaner.RunOnceAsync(CancellationToken.None);

        var call = Assert.Single(_repository.ClearCallsWithDiskState);
        Assert.Equal(oldFolder, call.Folder);
        // The folder must still have existed on disk at the moment ClearPathsUnderFolderAsync
        // was invoked — proving the DB clear happens before the disk delete, not after.
        Assert.True(call.FolderExistedAtClearTime);
        Assert.False(Directory.Exists(oldFolder));
    }

    [Fact]
    public async Task Leaves_the_folder_on_disk_and_retries_next_pass_when_the_db_clear_fails()
    {
        var oldFolder = CreateProcessedFolder("012345678905_20260601090000", new DateTime(2026, 6, 1));
        _repository.ThrowOnClear = true;
        var cleaner = CreateCleaner();

        await cleaner.RunOnceAsync(CancellationToken.None);

        // DB call failed, so the folder must be untouched (self-healing: it is still expired
        // and will be re-discovered and retried on the next pass).
        Assert.True(Directory.Exists(oldFolder));
        Assert.Empty(_repository.ClearedFolderPrefixes);

        // Simulate the next retention pass with a healthy repository: the same folder must
        // still be there to be re-discovered and cleaned up successfully.
        _repository.ThrowOnClear = false;
        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(oldFolder));
        Assert.Contains(oldFolder, _repository.ClearedFolderPrefixes);
    }

    [Fact]
    public async Task One_failing_processed_folder_does_not_prevent_other_expired_folders_from_being_cleaned_up()
    {
        var badFolder = CreateProcessedFolder("012345678905_20260601090000", new DateTime(2026, 6, 1));
        var goodFolder = CreateProcessedFolder("098765432109_20260601090000", new DateTime(2026, 6, 1));
        _repository.ThrowOnClearForFolder = folder => folder == badFolder;
        var cleaner = CreateCleaner();

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.True(Directory.Exists(badFolder));
        Assert.False(Directory.Exists(goodFolder));
        Assert.Contains(goodFolder, _repository.ClearedFolderPrefixes);
        Assert.DoesNotContain(badFolder, _repository.ClearedFolderPrefixes);
    }

    [Fact]
    public async Task One_failing_error_file_does_not_prevent_other_expired_error_files_from_being_cleaned_up()
    {
        var lockedFile = Path.Combine(_options.ErrorDirectory, "locked.csv");
        var deletableFile = Path.Combine(_options.ErrorDirectory, "deletable.csv");
        File.WriteAllText(lockedFile, "x");
        File.WriteAllText(deletableFile, "x");
        File.SetLastWriteTimeUtc(lockedFile, new DateTime(2026, 6, 1));
        File.SetLastWriteTimeUtc(deletableFile, new DateTime(2026, 6, 1));
        var cleaner = CreateCleaner();

        // Hold the "locked" file open with FileShare.None to force File.Delete to throw for
        // it specifically, simulating a locked-file/permission failure on one file only.
        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await cleaner.RunOnceAsync(CancellationToken.None);
        }

        Assert.True(File.Exists(lockedFile));
        Assert.False(File.Exists(deletableFile));
    }
}
