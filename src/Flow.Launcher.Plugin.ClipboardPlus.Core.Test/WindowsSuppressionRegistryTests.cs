// WindowsSuppressionRegistryTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class WindowsSuppressionRegistryTests
{
    [Fact]
    public void PinnedOrLastAvailableItemIsAlwaysVisible()
    {
        var registry = new WindowsSuppressionRegistry();
        registry.RememberAutomatic("synthetic-id", true);
        Assert.False(registry.ShouldHide("synthetic-id", true, true, true));
        Assert.False(registry.ShouldHide("synthetic-id", false, true, false));
        Assert.True(registry.ShouldHide("synthetic-id", false, true, true));
    }

    [Fact]
    public void AutomaticSuppressionStopsWhenDisabledButManualSuppressionStillWorks()
    {
        var registry = new WindowsSuppressionRegistry();
        registry.RememberAutomatic("automatic", true);
        registry.RememberManual("manual", true);
        Assert.False(registry.ShouldHide("automatic", false, false, true));
        Assert.True(registry.ShouldHide("manual", false, false, true));
        Assert.Equal(new[] { "manual" }, registry.GetManualIds());
    }

    [Fact]
    public void ForgettingPersistenceKeepsTheCurrentSessionOnly()
    {
        var registry = new WindowsSuppressionRegistry();
        registry.RememberManual("synthetic", true);
        registry.ForgetPersistence();
        Assert.True(registry.ShouldHide("synthetic", false, false, true));
        Assert.Empty(registry.GetPersistentEntries());
        Assert.False(new WindowsSuppressionRegistry(registry.GetPersistentEntries()).ShouldHide("synthetic", false, false, true));
    }

    [Fact]
    public void OnlyPersistentIdsSurviveReloadAndFailedReadsDoNotPrune()
    {
        var registry = new WindowsSuppressionRegistry();
        registry.RememberManual("session", false);
        registry.RememberManual("persistent", true);
        registry.RememberAutomatic("persistent", true);
        registry.PruneAbsent(Array.Empty<string>(), false);
        var reloaded = new WindowsSuppressionRegistry(registry.GetPersistentEntries());
        Assert.False(reloaded.ShouldHide("session", false, true, true));
        Assert.True(reloaded.ShouldHide("persistent", false, false, true));
        reloaded.PruneAbsent(Array.Empty<string>(), true);
        Assert.Empty(reloaded.GetPersistentEntries());
        Assert.False(reloaded.ShouldHide("persistent", false, true, true));
    }
}
