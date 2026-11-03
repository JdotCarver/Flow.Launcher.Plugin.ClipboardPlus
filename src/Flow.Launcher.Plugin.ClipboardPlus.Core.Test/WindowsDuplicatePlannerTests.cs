// WindowsDuplicatePlannerTests.cs
using Xunit;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class WindowsDuplicatePlannerTests
{
    private static ClipboardData Record(string text, int minute, bool pinned = false, bool saved = false) => new(text, DataType.PlainText, false)
    {
        HashId = Guid.NewGuid().ToString(), SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = pinned, Saved = saved
    };

    [Fact]
    public void PinnedRecordsCannotBeHiddenOrReplaceLastUnpinnedKeeper()
    {
        var items = new[]
        {
            new WindowsDuplicateEntry(Record("x", 4, pinned: true), "pinned"),
            new WindowsDuplicateEntry(Record("x", 3), "newest"),
            new WindowsDuplicateEntry(Record("x", 2), "older")
        };
        Assert.Equal(new[] { new WindowsDuplicateCandidate("older", 1, 2) }, WindowsDuplicatePlanner.Plan(items, true));
    }

    [Fact]
    public void IdenticalEmojiWithSameTimestampAndDistinctWindowsIdsPreservesPins()
    {
        var entries = new[]
        {
            new WindowsDuplicateEntry(Record("😀", 0), "{11111111-1111-4111-8111-111111111111}"),
            new WindowsDuplicateEntry(Record("😀", 0), "{22222222-2222-4222-8222-222222222222}"),
            new WindowsDuplicateEntry(Record("😀", 0, pinned: true), "{33333333-3333-4333-8333-333333333333}"),
            new WindowsDuplicateEntry(Record("😀", 0, pinned: true), "{44444444-4444-4444-8444-444444444444}")
        };
        Assert.Equal(new[] { new WindowsDuplicateCandidate(entries[1].WindowsId!, 0, 1) },
            WindowsDuplicatePlanner.Plan(entries, true));
    }

    [Fact]
    public void PluginDuplicatesAreNeverWindowsSuppressionTargets()
    {
        var items = new[]
        {
            new WindowsDuplicateEntry(Record("x", 3, saved: true), null),
            new WindowsDuplicateEntry(Record("x", 2, saved: true), null),
            new WindowsDuplicateEntry(Record("x", 1), "windows")
        };
        Assert.Equal(new[] { new WindowsDuplicateCandidate("windows", 0, 2) }, WindowsDuplicatePlanner.Plan(items, true));
    }

    [Fact]
    public void RepeatedWindowsIdsAreNeverHiddenOrUsedAsKeepers()
    {
        var items = new[]
        {
            new WindowsDuplicateEntry(Record("x", 4, pinned: true), "repeated"),
            new WindowsDuplicateEntry(Record("x", 3), "repeated"),
            new WindowsDuplicateEntry(Record("x", 2), "other")
        };
        Assert.Empty(WindowsDuplicatePlanner.Plan(items, true));
    }

    [Fact]
    public void NewestWindowsItemRemainsVisibleWhenOnlyOlderSavedEquivalentExists()
    {
        var items = new[]
        {
            new WindowsDuplicateEntry(Record("x", 1, saved: true), null),
            new WindowsDuplicateEntry(Record("x", 2), "windows")
        };
        Assert.Empty(WindowsDuplicatePlanner.Plan(items, true));
    }
    [Fact]
    public void ThoroughFrozenImageDuplicatesProduceAWindowsSuppressionCandidate()
    {
        ClipboardData Image(string id, int minute, bool pinned = false)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
            image.Freeze();
            return new ClipboardData(image, DataType.Image, false)
            {
                HashId = id, SenderApp = "synthetic", InitScore = 0,
                CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = pinned, Saved = false
            };
        }
        var items = new[] { new WindowsDuplicateEntry(Image("new", 2), "new"),
            new WindowsDuplicateEntry(Image("old", 1), "old") };
        Assert.Empty(WindowsDuplicatePlanner.Plan(items, false));
        Assert.Equal(new[] { new WindowsDuplicateCandidate("old", 0, 1) }, WindowsDuplicatePlanner.Plan(items, true));
    }
}
