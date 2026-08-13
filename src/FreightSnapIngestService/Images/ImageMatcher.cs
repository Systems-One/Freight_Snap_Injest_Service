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

        return Directory.EnumerateFiles(imageDirectory)
            .Where(path => Path.GetFileName(path).Contains(barcode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
