using FreightSnapIngestService.Models;
using Microsoft.Data.SqlClient;

namespace FreightSnapIngestService.Db;

public interface IFreightScanRepository
{
    Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct);
    Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct);
}

public class FreightScanRepository(string connectionString) : IFreightScanRepository
{
    public async Task<long> InsertScanAsync(
        ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using var insertScan = conn.CreateCommand();
        insertScan.Transaction = transaction;
        insertScan.CommandText = """
            INSERT INTO dbo.FreightScans (Barcode, ScanDateTime, Length, Width, Height, Volume, Weight, CsvPath)
            OUTPUT INSERTED.Id
            VALUES (@Barcode, @ScanDateTime, @Length, @Width, @Height, @Volume, @Weight, @CsvPath);
            """;
        insertScan.Parameters.AddWithValue("@Barcode", scan.Barcode);
        insertScan.Parameters.AddWithValue("@ScanDateTime", scan.ScanDateTime);
        insertScan.Parameters.AddWithValue("@Length", (object?)scan.Length ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@Width", (object?)scan.Width ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@Height", (object?)scan.Height ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@Volume", (object?)scan.Volume ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@Weight", (object?)scan.Weight ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@CsvPath", (object?)csvPath ?? DBNull.Value);

        var scanId = (long)(await insertScan.ExecuteScalarAsync(ct))!;

        foreach (var imagePath in imagePaths)
        {
            await using var insertImage = conn.CreateCommand();
            insertImage.Transaction = transaction;
            insertImage.CommandText = """
                INSERT INTO dbo.FreightScanImages (FreightScanId, ImagePath)
                VALUES (@FreightScanId, @ImagePath);
                """;
            insertImage.Parameters.AddWithValue("@FreightScanId", scanId);
            insertImage.Parameters.AddWithValue("@ImagePath", imagePath);
            await insertImage.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return scanId;
    }

    public async Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using var deleteImages = conn.CreateCommand();
        deleteImages.Transaction = transaction;
        deleteImages.CommandText = """
            DELETE FROM dbo.FreightScanImages
            WHERE ImagePath LIKE @Prefix + '%';
            """;
        deleteImages.Parameters.AddWithValue("@Prefix", folderPathPrefix);
        await deleteImages.ExecuteNonQueryAsync(ct);

        await using var clearCsvPath = conn.CreateCommand();
        clearCsvPath.Transaction = transaction;
        clearCsvPath.CommandText = """
            UPDATE dbo.FreightScans
            SET CsvPath = NULL
            WHERE CsvPath LIKE @Prefix + '%';
            """;
        clearCsvPath.Parameters.AddWithValue("@Prefix", folderPathPrefix);
        await clearCsvPath.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
    }
}
