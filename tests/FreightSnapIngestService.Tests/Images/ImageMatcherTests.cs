using FreightSnapIngestService.Images;
using Xunit;

namespace FreightSnapIngestService.Tests.Images;

public class ImageMatcherTests : IDisposable
{
    private readonly string _dir;
    private readonly ImageMatcher _matcher = new();

    public ImageMatcherTests()
    {
        _dir = Directory.CreateTempSubdirectory("image-matcher-tests-").FullName;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [Fact]
    public void Finds_all_files_whose_name_contains_the_barcode()
    {
        var a = Touch("012345678905_front.jpg");
        var b = Touch("012345678905_side.jpg");
        Touch("999999999999_front.jpg");

        var result = _matcher.FindMatchingImages(_dir, "012345678905");

        Assert.Equal(new[] { a, b }, result);
    }

    [Fact]
    public void Returns_results_sorted_ascending_by_file_name()
    {
        var second = Touch("012345678905_2.jpg");
        var first = Touch("012345678905_1.jpg");

        var result = _matcher.FindMatchingImages(_dir, "012345678905");

        Assert.Equal(new[] { first, second }, result);
    }

    [Fact]
    public void Returns_empty_list_when_no_images_match()
    {
        Touch("999999999999_front.jpg");

        var result = _matcher.FindMatchingImages(_dir, "012345678905");

        Assert.Empty(result);
    }

    [Fact]
    public void Does_not_match_a_shorter_barcode_that_is_a_substring_of_a_longer_one()
    {
        // Real production data has this collision: barcode "555" is a literal substring of "5555".
        Touch("5555_20260807_151343_1.jpg");

        var result = _matcher.FindMatchingImages(_dir, "555");

        Assert.Empty(result);
    }

    [Fact]
    public void Matches_a_barcode_delimited_by_non_digit_characters_even_when_a_colliding_longer_barcode_is_present()
    {
        var match = Touch("555_20260807_151435_1.jpg");
        Touch("5555_20260807_151343_1.jpg");

        var result = _matcher.FindMatchingImages(_dir, "555");

        Assert.Equal(new[] { match }, result);
    }
}
