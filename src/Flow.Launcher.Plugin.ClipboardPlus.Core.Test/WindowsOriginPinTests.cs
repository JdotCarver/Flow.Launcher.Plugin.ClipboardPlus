// WindowsOriginPinTests.cs
using System.Windows.Controls;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class WindowsOriginPinTests
{
    [Fact]
    public void PinAndUnpinPreserveOriginAndExcludeWindowsRecordsFromDeletion()
    {
        ClipboardData Record(string id, int minute, bool windows) => new("synthetic", DataType.PlainText, false)
        {
            HashId = id, SenderApp = "synthetic", InitScore = 0,
            CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = false, Saved = false,
            WindowsHistoryOrigin = windows
        };
        var pair = new ClipboardDataPair { ClipboardData = Record("windows", 1, true),
            PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Must remain lazy")) };
        pair.TogglePinned();
        Assert.True(pair.ClipboardData.Pinned);
        Assert.True(pair.ClipboardData.FromWindowsClipboardHistory());
        pair.TogglePinned();
        Assert.False(pair.ClipboardData.Pinned);
        Assert.True(pair.ClipboardData.FromWindowsClipboardHistory());
        Assert.Empty(DuplicateRecordPlanner.Plan(new[] { Record("plugin", 2, false), pair.ClipboardData }, true));
        Assert.Single(WindowsDuplicatePlanner.Plan(new[]
        {
            new WindowsDuplicateEntry(Record("plugin", 2, false), null),
            new WindowsDuplicateEntry(pair.ClipboardData, pair.ClipboardData.HashId)
        }, true));
    }
}
