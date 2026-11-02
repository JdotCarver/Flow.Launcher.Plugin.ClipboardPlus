// ClipboardCleanupSnapshotTests.cs
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class ClipboardCleanupSnapshotTests
{
    private static ClipboardData Record(object data, DataType type) => new(data, type, false)
    {
        HashId = "synthetic", SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1), Pinned = false, Saved = false
    };

    [Fact]
    public void FilePlanningIsDetachedAndMutationInvalidatesSnapshot()
    {
        var paths = new[] { "a", "b" };
        var record = Record(paths, DataType.Files);
        var snapshot = new ClipboardCleanupSnapshot(record);
        var planning = snapshot.CreatePlanningRecord();
        paths[0] = "changed";
        Assert.False(snapshot.Matches(record));
        Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(planning.Data));
    }

    [Fact]
    public void MetadataAndPinChangesInvalidateSnapshot()
    {
        var record = Record("synthetic", DataType.PlainText);
        var snapshot = new ClipboardCleanupSnapshot(record);
        Assert.True(snapshot.Matches(record));
        record.Pinned = true;
        Assert.False(snapshot.Matches(record));
        record.Pinned = false;
        record.CachedImagePath = "changed";
        Assert.False(snapshot.Matches(record));
    }

    [Fact]
    public void MutableImagesAreNeverConsideredStable()
    {
        var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        var record = Record(bitmap, DataType.Image);
        var snapshot = new ClipboardCleanupSnapshot(record);
        Assert.False(snapshot.Matches(record));
        bitmap.Freeze();
        Assert.False(snapshot.Matches(record));
        Assert.True(new ClipboardCleanupSnapshot(record).Matches(record));
    }
}
