using FreightSnapIngestService.Naming;
using Xunit;

namespace FreightSnapIngestService.Tests.Naming;

public class ScanFileNamerTests
{
    private readonly ScanFileNamer _namer = new();
    private static readonly DateTime ScanDateTime = new(2026, 8, 13, 9, 15, 0);

    [Fact]
    public void Builds_processed_folder_name_as_barcode_underscore_timestamp()
    {
        var name = _namer.BuildProcessedFolderName("012345678905", ScanDateTime);
        Assert.Equal("012345678905_20260813091500", name);
    }

    [Fact]
    public void Builds_csv_file_name_matching_the_folder_name_with_csv_extension()
    {
        var name = _namer.BuildCsvFileName("012345678905", ScanDateTime);
        Assert.Equal("012345678905_20260813091500.csv", name);
    }

    [Theory]
    [InlineData(1, ".jpg", "012345678905_20260813091500_1.jpg")]
    [InlineData(2, ".png", "012345678905_20260813091500_2.png")]
    public void Builds_image_file_name_with_sequence_number_and_original_extension(
        int sequenceNumber, string extension, string expected)
    {
        var name = _namer.BuildImageFileName("012345678905", ScanDateTime, sequenceNumber, extension);
        Assert.Equal(expected, name);
    }
}
