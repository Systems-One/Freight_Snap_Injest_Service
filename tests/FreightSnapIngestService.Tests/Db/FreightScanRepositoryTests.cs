using FreightSnapIngestService.Db;
using FreightSnapIngestService.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FreightSnapIngestService.Tests.Db;

public class FreightScanRepositoryTests : IClassFixture<SqlServerTestFixture>
{
    private readonly SqlServerTestFixture _fixture;

    public FreightScanRepositoryTests(SqlServerTestFixture fixture) => _fixture = fixture;

    private static readonly ScanRecord Scan = new(
        "012345678905", new DateTime(2026, 8, 13, 9, 15, 0), 12.5m, 8.0m, 6.25m, 625.0m, 14.2m);

    [Fact]
    public async Task InsertScanAsync_writes_the_scan_and_its_images()
    {
        var repository = new FreightScanRepository(_fixture.ConnectionString);
        var csvPath = @"C:\FreightSnap\processed\012345678905_20260813091500\012345678905_20260813091500.csv";
        var imagePaths = new[]
        {
            @"C:\FreightSnap\processed\012345678905_20260813091500\012345678905_20260813091500_1.jpg",
            @"C:\FreightSnap\processed\012345678905_20260813091500\012345678905_20260813091500_2.jpg",
        };

        var scanId = await repository.InsertScanAsync(Scan, csvPath, imagePaths, CancellationToken.None);

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText = "SELECT Barcode, CsvPath FROM dbo.FreightScans WHERE Id = @Id";
        scanCmd.Parameters.AddWithValue("@Id", scanId);
        await using var reader = await scanCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("012345678905", reader.GetString(0));
        Assert.Equal(csvPath, reader.GetString(1));
        await reader.CloseAsync();

        await using var imageCmd = conn.CreateCommand();
        imageCmd.CommandText = "SELECT COUNT(*) FROM dbo.FreightScanImages WHERE FreightScanId = @Id";
        imageCmd.Parameters.AddWithValue("@Id", scanId);
        var imageCount = (int)(await imageCmd.ExecuteScalarAsync())!;
        Assert.Equal(2, imageCount);
    }

    [Fact]
    public async Task ClearPathsUnderFolderAsync_nulls_csv_path_and_deletes_image_rows_under_the_folder()
    {
        var repository = new FreightScanRepository(_fixture.ConnectionString);
        var folder = @"C:\FreightSnap\processed\999999999999_20260813120000";
        var csvPath = $@"{folder}\999999999999_20260813120000.csv";
        var imagePath = $@"{folder}\999999999999_20260813120000_1.jpg";
        var otherScan = Scan with { Barcode = "999999999999" };
        var scanId = await repository.InsertScanAsync(otherScan, csvPath, new[] { imagePath }, CancellationToken.None);

        await repository.ClearPathsUnderFolderAsync(folder, CancellationToken.None);

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText = "SELECT CsvPath FROM dbo.FreightScans WHERE Id = @Id";
        scanCmd.Parameters.AddWithValue("@Id", scanId);
        var csvPathAfter = await scanCmd.ExecuteScalarAsync();
        Assert.Equal(DBNull.Value, csvPathAfter);

        await using var imageCmd = conn.CreateCommand();
        imageCmd.CommandText = "SELECT COUNT(*) FROM dbo.FreightScanImages WHERE FreightScanId = @Id";
        imageCmd.Parameters.AddWithValue("@Id", scanId);
        var imageCount = (int)(await imageCmd.ExecuteScalarAsync())!;
        Assert.Equal(0, imageCount);
    }

    [Fact]
    public async Task ClearPathsUnderFolderAsync_does_not_clear_a_different_scan_whose_path_only_matches_via_the_underscore_wildcard()
    {
        // SQL LIKE treats '_' as "match any single character." The real folder-naming
        // convention is "{barcode}_{timestamp}", so every prefix contains underscores —
        // ClearPathsUnderFolderAsync must escape them, or a prefix like ".../AAA_1" would
        // also match an unrelated path like ".../AAAX1" purely via wildcard substitution.
        var repository = new FreightScanRepository(_fixture.ConnectionString);
        var targetFolder = @"C:\FreightSnap\processed\AAA_1";
        var targetCsvPath = $@"{targetFolder}\AAA_1.csv";
        var targetScan = Scan with { Barcode = "AAA1TARGET" };
        var targetScanId = await repository.InsertScanAsync(targetScan, targetCsvPath, Array.Empty<string>(), CancellationToken.None);

        var otherCsvPath = @"C:\FreightSnap\processed\AAAX1\AAAX1.csv"; // same length, '_' replaced by 'X'
        var otherScan = Scan with { Barcode = "AAAXOTHER" };
        var otherScanId = await repository.InsertScanAsync(otherScan, otherCsvPath, Array.Empty<string>(), CancellationToken.None);

        await repository.ClearPathsUnderFolderAsync(targetFolder, CancellationToken.None);

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var targetCmd = conn.CreateCommand();
        targetCmd.CommandText = "SELECT CsvPath FROM dbo.FreightScans WHERE Id = @Id";
        targetCmd.Parameters.AddWithValue("@Id", targetScanId);
        Assert.Equal(DBNull.Value, await targetCmd.ExecuteScalarAsync());

        await using var otherCmd = conn.CreateCommand();
        otherCmd.CommandText = "SELECT CsvPath FROM dbo.FreightScans WHERE Id = @Id";
        otherCmd.Parameters.AddWithValue("@Id", otherScanId);
        Assert.Equal(otherCsvPath, await otherCmd.ExecuteScalarAsync());
    }
}
