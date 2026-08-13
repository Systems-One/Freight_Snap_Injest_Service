using FreightSnapIngestService.Pending;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Pending;

public class PendingScanTrackerTests
{
    [Fact]
    public void HasTimedOut_is_false_immediately_after_first_seen()
    {
        var time = new FakeTimeProvider();
        var tracker = new PendingScanTracker(time);

        tracker.TrackFirstSeen(@"C:\csv\a.csv");

        Assert.False(tracker.HasTimedOut(@"C:\csv\a.csv", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void HasTimedOut_is_true_once_the_timeout_elapses()
    {
        var time = new FakeTimeProvider();
        var tracker = new PendingScanTracker(time);

        tracker.TrackFirstSeen(@"C:\csv\a.csv");
        time.Advance(TimeSpan.FromMinutes(31));

        Assert.True(tracker.HasTimedOut(@"C:\csv\a.csv", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void TrackFirstSeen_does_not_reset_an_already_tracked_file()
    {
        var time = new FakeTimeProvider();
        var tracker = new PendingScanTracker(time);

        tracker.TrackFirstSeen(@"C:\csv\a.csv");
        time.Advance(TimeSpan.FromMinutes(20));
        tracker.TrackFirstSeen(@"C:\csv\a.csv");
        time.Advance(TimeSpan.FromMinutes(20));

        Assert.True(tracker.HasTimedOut(@"C:\csv\a.csv", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Remove_clears_the_tracked_entry()
    {
        var time = new FakeTimeProvider();
        var tracker = new PendingScanTracker(time);
        tracker.TrackFirstSeen(@"C:\csv\a.csv");

        tracker.Remove(@"C:\csv\a.csv");

        Assert.Throws<InvalidOperationException>(
            () => tracker.HasTimedOut(@"C:\csv\a.csv", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void HasTimedOut_throws_for_an_untracked_file()
    {
        var tracker = new PendingScanTracker(new FakeTimeProvider());

        Assert.Throws<InvalidOperationException>(
            () => tracker.HasTimedOut(@"C:\csv\unknown.csv", TimeSpan.FromMinutes(30)));
    }
}
