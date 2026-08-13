// tests/FreightSnapIngestService.Tests/Stability/FileStabilityTrackerTests.cs
using FreightSnapIngestService.Stability;
using Xunit;

namespace FreightSnapIngestService.Tests.Stability;

public class FileStabilityTrackerTests
{
    [Fact]
    public void First_observation_of_a_file_is_never_stable()
    {
        var tracker = new FileStabilityTracker();

        Assert.False(tracker.IsStable(@"C:\csv\a.csv", 100));
    }

    [Fact]
    public void Same_size_on_the_next_observation_is_stable()
    {
        var tracker = new FileStabilityTracker();

        tracker.IsStable(@"C:\csv\a.csv", 100);
        var result = tracker.IsStable(@"C:\csv\a.csv", 100);

        Assert.True(result);
    }

    [Fact]
    public void Changed_size_on_the_next_observation_is_not_stable()
    {
        var tracker = new FileStabilityTracker();

        tracker.IsStable(@"C:\csv\a.csv", 100);
        var result = tracker.IsStable(@"C:\csv\a.csv", 150);

        Assert.False(result);
    }

    [Fact]
    public void After_reporting_stable_the_next_observation_starts_over()
    {
        var tracker = new FileStabilityTracker();

        tracker.IsStable(@"C:\csv\a.csv", 100);
        tracker.IsStable(@"C:\csv\a.csv", 100); // stable, forgets it
        var result = tracker.IsStable(@"C:\csv\a.csv", 100);

        Assert.False(result);
    }
}
