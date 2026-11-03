// WindowsPinSuppressionSafetyTests.cs
using System.Reflection;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class WindowsPinSuppressionSafetyTests
{
    [Fact]
    public void OnlyVerifiedUnpinnedIdsCanBeSuppressed()
    {
        var helper = new WindowsClipboardHelper();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var trust = typeof(WindowsClipboardHelper).GetField("pinStateTrusted", flags)!;
        var pins = typeof(WindowsClipboardHelper).GetField("knownPinnedItemIds", flags)!;
        pins.SetValue(helper, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "synthetic-pinned" });
        Assert.False(helper.IsKnownUnpinned("synthetic-unpinned"));
        trust.SetValue(helper, true);
        Assert.False(helper.IsKnownUnpinned("SYNTHETIC-PINNED"));
        Assert.False(helper.IsKnownUnpinned(""));
        Assert.True(helper.IsKnownUnpinned("synthetic-unpinned"));
        trust.SetValue(helper, false);
        Assert.False(helper.IsKnownUnpinned("synthetic-unpinned"));
    }
}
