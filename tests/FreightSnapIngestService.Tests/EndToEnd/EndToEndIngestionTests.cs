using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Ingestion;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Retention;
using FreightSnapIngestService.Stability;
using FreightSnapIngestService.Tests.Db;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.EndToEnd;

public class EndToEndIngestionTests : IClassFixture<SqlServerTestFixture>, IDisposable
{
    private readonly SqlServerTestFixture _sqlFixture;
    private readonly string _root;
    private readonly FreightIngestOptions _options;
    private readonly FakeTimeProvider _time = new();
    private readonly IFreightScanRepository _repository;
    private readonly IngestPipeline _pipeline;
    private readonly RetentionCleaner _retentionCleaner;

    public EndToEndIngestionTests(SqlServerTestFixture sqlFixture)
    {
        _sqlFixture = sqlFixture;
        _root = Directory.CreateTempSubdirectory("e2e-ingest-tests-").FullName;
        _options = new FreightIngestOptions
        {
            CsvDirectory = Path.Combine(_root, "csv"),
            ImageDirectory = Path.Combine(_root, "images"),
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            PendingTimeoutMinutes = 30,
            RetentionDays = 30,
        };
        Directory.CreateDirectory(_options.CsvDirectory);
        Directory.CreateDirectory(_options.ImageDirectory);

        _repository = new FreightScanRepository(_sqlFixture.ConnectionString);
        _pipeline = new IngestPipeline(
            _options,
            new CsvScanParser(),
            new ImageMatcher(),
            new ScanFileNamer(),
            new PendingScanTracker(_time),
            new FileStabilityTracker(),
            _repository,
            NullLogger<IngestPipeline>.Instance);
        _retentionCleaner = new RetentionCleaner(_options, _repository, _time, NullLogger<RetentionCleaner>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

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

    private async Task<(DateTime? ScanDateTime, decimal? Length, decimal? Width, decimal? Height, decimal? Weight, string? CsvPath, int ImageCount)> ReadScanAsync(string barcode)
    {
        await using var conn = new SqlConnection(_sqlFixture.ConnectionString);
        await conn.OpenAsync();

        await using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText =
            "SELECT TOP 1 Id, ScanDateTime, Length, Width, Height, Weight, CsvPath FROM dbo.FreightScans WHERE Barcode = @Barcode ORDER BY Id DESC";
        scanCmd.Parameters.AddWithValue("@Barcode", barcode);
        await using var reader = await scanCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return (null, null, null, null, null, null, 0);

        var scanId = reader.GetInt64(0);
        var scanDateTime = reader.GetDateTime(1);
        var length = reader.IsDBNull(2) ? (decimal?)null : reader.GetDecimal(2);
        var width = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3);
        var height = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4);
        var weight = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5);
        var csvPath = reader.IsDBNull(6) ? null : reader.GetString(6);
        await reader.CloseAsync();

        await using var imageCmd = conn.CreateCommand();
        imageCmd.CommandText = "SELECT COUNT(*) FROM dbo.FreightScanImages WHERE FreightScanId = @Id";
        imageCmd.Parameters.AddWithValue("@Id", scanId);
        var imageCount = (int)(await imageCmd.ExecuteScalarAsync())!;

        return (scanDateTime, length, width, height, weight, csvPath, imageCount);
    }

    [Fact]
    public async Task Ingests_a_realistic_multi_image_scan_matching_production_CSV_and_image_formats()
    {
        // Real row from Demo_Data/SWAD_FILES/20260804_144435_1.csv: headerless, yyyyMMddHHmmss datetime.
        WriteCsv("scan.csv", "724076,20260804144415,122.0,101.5,128.5,1.591216,216.000\n");
        // Real image naming convention from Demo_Data/Images: <barcode>_<yyyyMMdd>_<HHmmss>_<n>.jpg
        WriteImage("724076_20260804_144420_1.jpg");
        WriteImage("724076_20260804_144420_2.jpg");

        await _pipeline.ProcessOnceAsync(CancellationToken.None); // stability baseline
        await _pipeline.ProcessOnceAsync(CancellationToken.None); // finalizes

        var scan = await ReadScanAsync("724076");
        Assert.Equal(new DateTime(2026, 8, 4, 14, 44, 15), scan.ScanDateTime);
        Assert.Equal(2, scan.ImageCount);
        Assert.NotNull(scan.CsvPath);
        Assert.True(File.Exists(scan.CsvPath));

        var processedFolder = Path.Combine(_options.ProcessedDirectory, "724076_20260804144415");
        Assert.True(File.Exists(Path.Combine(processedFolder, "724076_20260804144415_1.jpg")));
        Assert.True(File.Exists(Path.Combine(processedFolder, "724076_20260804144415_2.jpg")));
    }

    [Fact]
    public async Task Does_not_cross_match_images_between_a_barcode_and_its_digit_substring_collision()
    {
        // Real Demo_Data/Images collision: barcode "555" is a literal substring of barcode "5555".
        WriteCsv("scan.csv", "555,20260807151435,50.0,40.0,30.0,0.060000,5.000\n");
        WriteImage("555_20260807_151435_1.jpg");
        WriteImage("555_20260807_151435_2.jpg");
        WriteImage("5555_20260807_151343_1.jpg");
        WriteImage("5555_20260807_151343_2.jpg");

        await _pipeline.ProcessOnceAsync(CancellationToken.None);
        await _pipeline.ProcessOnceAsync(CancellationToken.None);

        var scan = await ReadScanAsync("555");
        Assert.Equal(2, scan.ImageCount); // only its own two images, not the 5555 pair

        // The unrelated barcode's images were never touched by the "555" scan.
        Assert.True(File.Exists(Path.Combine(_options.ImageDirectory, "5555_20260807_151343_1.jpg")));
        Assert.True(File.Exists(Path.Combine(_options.ImageDirectory, "5555_20260807_151343_2.jpg")));
    }

    [Fact]
    public async Task Finalizes_a_noisy_barcode_without_images_after_the_pending_timeout()
    {
        // Real Demo_Data anomaly: a barcode field with extra leading characters
        // (Demo_Data/SWAD_FILES/20260804_144537_1.csv).
        WriteCsv("scan.csv", "vvhjmfvjhfg724076,20260804144524,122.0,101.5,128.5,1.591216,216.000\n");

        await _pipeline.ProcessOnceAsync(CancellationToken.None);
        await _pipeline.ProcessOnceAsync(CancellationToken.None); // now pending, no images
        _time.Advance(TimeSpan.FromMinutes(31));
        await _pipeline.ProcessOnceAsync(CancellationToken.None);

        var scan = await ReadScanAsync("vvhjmfvjhfg724076");
        Assert.Equal(0, scan.ImageCount);
        Assert.NotNull(scan.CsvPath);
    }

    [Fact]
    public async Task Routes_a_truncated_production_style_row_to_the_error_directory()
    {
        // Real row shape with the trailing Weight field dropped (simulates a truncated write).
        WriteCsv("truncated.csv", "714826,20260812213052,114.0,103.0,167.5,1.966785\n");

        await _pipeline.ProcessOnceAsync(CancellationToken.None);
        await _pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_options.ErrorDirectory, "truncated.csv")));
        var scan = await ReadScanAsync("714826");
        Assert.Null(scan.ScanDateTime);
    }

    [Fact]
    public async Task Retention_clears_file_paths_but_keeps_the_scan_row_after_files_expire()
    {
        // Uses a distinct barcode from the truncated-row test above: both tests share one
        // SqlServerTestFixture database (IClassFixture is instantiated once per class), so
        // reusing a barcode+timestamp across tests would make correctness depend on xunit's
        // (unguaranteed) method execution order.
        WriteCsv("scan.csv", "695774,20260812213052,114.0,103.0,167.5,1.966785,215.000\n");
        WriteImage("695774_20260812_213100_1.jpg");

        await _pipeline.ProcessOnceAsync(CancellationToken.None);
        await _pipeline.ProcessOnceAsync(CancellationToken.None);

        var processedFolder = Path.Combine(_options.ProcessedDirectory, "695774_20260812213052");
        Directory.SetLastWriteTimeUtc(processedFolder, DateTime.UtcNow.AddDays(-31));
        _time.SetUtcNow(DateTimeOffset.UtcNow);

        await _retentionCleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(processedFolder));
        var scan = await ReadScanAsync("695774");
        Assert.Equal(new DateTime(2026, 8, 12, 21, 30, 52), scan.ScanDateTime); // scan row still present
        Assert.Equal(114.0m, scan.Length); // dimension data survives retention untouched
        Assert.Equal(103.0m, scan.Width);
        Assert.Equal(167.5m, scan.Height);
        Assert.Equal(215.000m, scan.Weight);
        Assert.Null(scan.CsvPath); // path cleared
        Assert.Equal(0, scan.ImageCount); // image rows cleared
    }
}
