// WindowsSuppressionIntegrationTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class WindowsSuppressionIntegrationTests
{
    private static ClipboardData Record(string id, int minute) => new("synthetic", DataType.PlainText, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = false, Saved = false
    };

    [Fact]
    public void SimulatedPinRemovalAndReloadFailOpenWithoutChangingHistory()
    {
        var items = new[] { Record("newer", 2), Record("older", 1) };
        var plan = WindowsDuplicatePlanner.Plan(new[]
        {
            new WindowsDuplicateEntry(items[0], "newer"), new WindowsDuplicateEntry(items[1], "older")
        }, true);
        var candidate = Assert.Single(plan);
        var keeperSnapshot = new ClipboardCleanupSnapshot(items[candidate.KeeperIndex]);
        var targetSnapshot = new ClipboardCleanupSnapshot(items[candidate.WindowsIndex]);
        var registry = new WindowsSuppressionRegistry();
        registry.RememberManual(candidate.WindowsId, true);
        bool Hidden(bool keeperPresent) => registry.ShouldHide(candidate.WindowsId, items[1].Pinned, false,
            keeperPresent && keeperSnapshot.Matches(items[0]) && targetSnapshot.Matches(items[1]));
        Assert.True(Hidden(true));
        items[1].Pinned = true;
        Assert.False(Hidden(true));
        items[1].Pinned = false;
        Assert.False(Hidden(false));
        registry.PruneAbsent(new[] { "newer" }, false);
        Assert.Single(registry.GetPersistentEntries());
        var reloaded = new WindowsSuppressionRegistry(registry.GetPersistentEntries());
        Assert.True(reloaded.ShouldHide("older", false, false, true));
        reloaded.PruneAbsent(new[] { "newer" }, true);
        Assert.Empty(reloaded.GetPersistentEntries());
        Assert.Equal(2, items.Length);
    }
}
