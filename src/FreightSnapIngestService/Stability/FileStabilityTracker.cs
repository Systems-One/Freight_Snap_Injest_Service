using System.Collections.Concurrent;

namespace FreightSnapIngestService.Stability;

public class FileStabilityTracker
{
    private readonly ConcurrentDictionary<string, long> _lastSeenSizes = new();

    public bool IsStable(string filePath, long currentSize)
    {
        if (_lastSeenSizes.TryGetValue(filePath, out var lastSize) && lastSize == currentSize)
        {
            _lastSeenSizes.TryRemove(filePath, out _);
            return true;
        }

        _lastSeenSizes[filePath] = currentSize;
        return false;
    }
}
