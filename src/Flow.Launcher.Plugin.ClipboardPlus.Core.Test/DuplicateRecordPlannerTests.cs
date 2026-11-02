// DuplicateRecordPlannerTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class DuplicateRecordPlannerTests
{
    private static ClipboardData Text(string text, int minute, bool pinned = false, bool saved = false) => new(text, DataType.PlainText, false)
    {
        HashId = Guid.NewGuid().ToString(), SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = pinned, Saved = saved
    };

    [Fact]
    public void PinnedRecordsAreProtectedAndDoNotReplaceUnpinnedKeeper()
    {
        var records = new[] { Text("same", 4, pinned: true), Text("same", 3), Text("same", 2), Text("different", 1) };
        Assert.Equal(new[] { new DuplicateRecordCandidate(1, 2) }, DuplicateRecordPlanner.Plan(records, true));
    }

    [Fact]
    public void NewestTemporaryDoesNotCauseSavedHistoryToBeRemoved()
    {
        var records = new[] { Text("same", 4), Text("same", 3, saved: true), Text("same", 2), Text("same", 1, saved: true) };
        Assert.Equal(new[] { new DuplicateRecordCandidate(0, 2) }, DuplicateRecordPlanner.Plan(records, true));
    }

    [Fact]
    public void SavedNewestKeepsItsPersistenceAndOlderUnpinnedDuplicatesCanBePlanned()
    {
        var records = new[] { Text("same", 1, saved: true), Text("same", 3, saved: true), Text("same", 2) };
        Assert.Equal(new[] { new DuplicateRecordCandidate(1, 2), new DuplicateRecordCandidate(1, 0) }, DuplicateRecordPlanner.Plan(records, true));
    }

    [Fact]
    public void HashCollisionsNeverMergeDistinctContent()
    {
        ClipboardData Files(string[] paths, int minute) => new(paths, DataType.Files, false)
        {
            HashId = Guid.NewGuid().ToString(), SenderApp = "synthetic", InitScore = 0,
            CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = false, Saved = false
        };
        var records = new[] { Files(new[] { "a\nb", "c" }, 3), Files(new[] { "a", "b\nc" }, 2), Files(new[] { "a", "b\nc" }, 1) };
        Assert.Equal(new[] { new DuplicateRecordCandidate(1, 2) }, DuplicateRecordPlanner.Plan(records, true));
    }
}
