using System.Text.RegularExpressions;

namespace FreightSnapIngestService.Images;

public interface IImageMatcher
{
    IReadOnlyList<string> FindMatchingImages(string imageDirectory, string barcode);
}

public class ImageMatcher : IImageMatcher
{
    public IReadOnlyList<string> FindMatchingImages(string imageDirectory, string barcode)
    {
        if (!Directory.Exists(imageDirectory))
            return Array.Empty<string>();

        var pattern = $@"(?<!\d){Regex.Escape(barcode)}(?!\d)";

        return Directory.EnumerateFiles(imageDirectory)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), pattern, RegexOptions.IgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
