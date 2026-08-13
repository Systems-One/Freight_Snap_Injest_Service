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
}
