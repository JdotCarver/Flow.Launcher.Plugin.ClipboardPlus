// HistoryOverflowPolicyTests.cs
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class HistoryOverflowPolicyTests
{
    private static ClipboardData Record(object data, DataType type, string id, bool saved = false) => new(data, type, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = 0, CreateTime = new DateTime(2026, 1, 1), Pinned = false, Saved = saved
    };

    [Fact]
    public void OnlyUnverifiedGroupsReserveBoundedOverflow()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
        image.Freeze();
        var data = new[] { Record(image, DataType.Image, "a"), Record(image, DataType.Image, "b"),
            Record("text", DataType.PlainText, "c", true), Record("text", DataType.PlainText, "d") };
        var snapshots = data.Select(record => new ClipboardCleanupSnapshot(record)).ToArray();
        Assert.Equal(1, HistoryOverflowPolicy.CountUncertain(snapshots, false, new HashSet<int>()));
        Assert.Equal(0, HistoryOverflowPolicy.CountUncertain(snapshots, true, new HashSet<int>()));
        Assert.Equal(1, HistoryOverflowPolicy.CountUncertain(snapshots, true, new HashSet<int> { 2 }));
        Assert.Equal(27, HistoryOverflowPolicy.Limit(2, 1000));
        Assert.Equal(int.MaxValue, HistoryOverflowPolicy.Limit(int.MaxValue, 25));
    }
}
