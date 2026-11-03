// DuplicateCleanupEngineTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class DuplicateCleanupEngineTests
{
    private static ClipboardData Text(string id, int minute) => new("synthetic", DataType.PlainText, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = false, Saved = false
    };

    [Fact]
    public async Task ReportsIndependentSuccessesSkipsAndFailuresWithoutAborting()
    {
        var snapshots = new[] { Text("a", 4), Text("b", 3), Text("c", 2), Text("d", 1) }
            .Select(data => new ClipboardCleanupSnapshot(data)).ToArray();
        var calls = new List<int>();
        var updates = new List<CleanupReport>();
        var attempts = new List<DuplicateRemovalResult?>();
        var result = await DuplicateCleanupEngine.RunAsync(snapshots, true, (keeper, duplicate) =>
        {
            Assert.Equal(0, keeper);
            calls.Add(duplicate);
            if (duplicate == 2) throw new InvalidOperationException("private synthetic content");
            return Task.FromResult(duplicate == 1 ? DuplicateRemovalResult.Removed : DuplicateRemovalResult.Skipped);
        }, updates.Add, (_, _, outcome) => attempts.Add(outcome));
        Assert.Equal(new[] { 1, 2, 3 }, calls);
        Assert.Single(updates);
        Assert.Equal(new DuplicateRemovalResult?[] { DuplicateRemovalResult.Removed, null, DuplicateRemovalResult.Skipped }, attempts);
        Assert.Equal(new CleanupReport(1, 0, 0, 1, 1), updates[^1]);
        Assert.Equal(new CleanupReport(1, 0, 0, 1, 1), result);
    }

    [Fact]
    public async Task IdenticalEmojiAtTheSameTimeKeepsEveryPinnedRecord()
    {
        var ids = new[]
        {
            "{11111111-1111-4111-8111-111111111111}",
            "{22222222-2222-4222-8222-222222222222}",
            "{33333333-3333-4333-8333-333333333333}",
            "{44444444-4444-4444-8444-444444444444}"
        };
        var records = ids.Select(id => new ClipboardData("😀", DataType.PlainText, false)
        {
            HashId = id, SenderApp = "synthetic", InitScore = 0,
            CreateTime = new DateTime(2026, 1, 1), Pinned = false, Saved = false
        }).ToArray();
        records[2].Pinned = true;
        records[3].Pinned = true;
        var snapshots = records.Select(data => new ClipboardCleanupSnapshot(data)).ToArray();
        var removed = new List<int>();

        var report = await DuplicateCleanupEngine.RunAsync(snapshots, true, (keeper, duplicate) =>
        {
            Assert.Equal(0, keeper);
            removed.Add(duplicate);
            return Task.FromResult(DuplicateRemovalResult.Removed);
        });

        Assert.Equal(new[] { 1 }, removed);
        Assert.Equal(new CleanupReport(1, 0, 0, 0, 0), report);
    }

    [Fact]
    public async Task OwnerCanRejectAChangedRecordWithoutRemovingIt()
    {
        var records = new[] { Text("keeper", 2), Text("duplicate", 1) };
        var snapshots = records.Select(data => new ClipboardCleanupSnapshot(data)).ToArray();
        records[1].Pinned = true;
        var report = await DuplicateCleanupEngine.RunAsync(snapshots, true, (keeper, duplicate) =>
            Task.FromResult(snapshots[keeper].Matches(records[keeper]) && snapshots[duplicate].Matches(records[duplicate])
                ? DuplicateRemovalResult.Removed : DuplicateRemovalResult.Skipped));
        Assert.Equal(new CleanupReport(0, 0, 0, 1, 0), report);
    }
}
