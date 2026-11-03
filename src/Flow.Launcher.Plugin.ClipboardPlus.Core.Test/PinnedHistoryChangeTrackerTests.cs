// PinnedHistoryChangeTrackerTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class PinnedHistoryChangeTrackerTests
{
    [Fact]
    public void RecoveryIsNotAnUnpinEventButLaterChangesAreReported()
    {
        var tracker = new PinnedHistoryChangeTracker();
        string[] items = ["first", "second"];
        tracker.Reset(["first"], true);
        Assert.Empty(tracker.Observe(items, [], false));
        Assert.Empty(tracker.Observe(items, [], true));
        Assert.Equal(new[] { ("second", true) }, tracker.Observe(items, ["second"], true));
        Assert.Equal(new[] { ("second", false) }, tracker.Observe(items, [], true));
    }

    [Fact]
    public void UntrustedReadOutsideEventInvalidatesThePreviousBaseline()
    {
        var tracker = new PinnedHistoryChangeTracker();
        tracker.Reset(["FIRST"], true);
        Assert.Empty(tracker.Observe(["first"], ["first"], true));
        tracker.Invalidate();
        Assert.Empty(tracker.Observe(["first"], [], true));
    }
}
