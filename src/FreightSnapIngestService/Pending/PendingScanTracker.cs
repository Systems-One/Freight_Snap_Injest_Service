using System.Collections.Concurrent;

namespace FreightSnapIngestService.Pending;

public class PendingScanTracker(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstSeenUtc = new();

    public void TrackFirstSeen(string csvFilePath) =>
        _firstSeenUtc.TryAdd(csvFilePath, timeProvider.GetUtcNow());

    public bool HasTimedOut(string csvFilePath, TimeSpan timeout)
    {
        if (!_firstSeenUtc.TryGetValue(csvFilePath, out var firstSeen))
            throw new InvalidOperationException($"'{csvFilePath}' is not being tracked.");

        return timeProvider.GetUtcNow() - firstSeen >= timeout;
    }

    public void Remove(string csvFilePath) => _firstSeenUtc.TryRemove(csvFilePath, out _);
}
