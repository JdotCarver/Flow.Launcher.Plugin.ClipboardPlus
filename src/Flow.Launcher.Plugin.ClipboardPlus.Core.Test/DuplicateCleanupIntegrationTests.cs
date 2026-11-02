// DuplicateCleanupIntegrationTests.cs
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

[Collection("Bulk insert isolation")]
public class DuplicateCleanupIntegrationTests
{
    private static ClipboardData Record(string id, int minute, bool saved = true, bool pinned = false) => new("synthetic", DataType.PlainText, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = pinned, Saved = saved,
        EncryptKeyMd5 = StringUtils.EncryptKeyMd5
    };

    [Fact]
    public async Task SavedCandidatesCommitIndependentlyAndDatabaseFailureRollsBackItsCandidate()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = new SqliteDatabase("synthetic-cleanup", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        await database.InitializeDatabaseAsync();
        var live = new List<ClipboardData> { Record("keeper", 3), Record("first", 2), Record("second", 1), Record("pinned", 4, pinned: true) };
        await database.AddRecordsAsync(live, true);
        database.Connection.Execute("""
            CREATE TRIGGER fail_second_asset BEFORE DELETE ON asset
            WHEN OLD.hash_id = 'second'
            BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;
            """);
        var snapshots = live.Select(item => new ClipboardCleanupSnapshot(item)).ToArray();
        var report = await DuplicateCleanupEngine.RunAsync(snapshots, true, async (keeperIndex, duplicateIndex) =>
        {
            var keeper = live.SingleOrDefault(item => item.HashId == snapshots[keeperIndex].Original.HashId);
            var duplicate = live.SingleOrDefault(item => item.HashId == snapshots[duplicateIndex].Original.HashId);
            if (!snapshots[keeperIndex].Matches(keeper) || !snapshots[duplicateIndex].Matches(duplicate)) return DuplicateRemovalResult.Skipped;
            if (!await database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true)) return DuplicateRemovalResult.Skipped;
            live.RemoveAll(item => item.HashId == duplicate.HashId);
            return DuplicateRemovalResult.Removed;
        });
        Assert.Equal(new CleanupReport(1, 0, 0, 0, 1), report);
        Assert.Equal(new[] { "keeper", "second", "pinned" }, live.Select(item => item.HashId));
        Assert.Equal(3, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record"));
        Assert.Equal(1, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM asset WHERE hash_id = 'second'"));
    }

    [Fact]
    public async Task ChangedPinAndTemporaryKeeperNeverDeleteSavedRecords()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = new SqliteDatabase("synthetic-stale", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        await database.InitializeDatabaseAsync();
        var keeper = Record("keeper", 3);
        var duplicate = Record("duplicate", 2);
        await database.AddRecordsAsync(new[] { keeper, duplicate }, true);
        var snapshots = new[] { new ClipboardCleanupSnapshot(keeper), new ClipboardCleanupSnapshot(duplicate) };
        duplicate.Pinned = true;
        var report = await DuplicateCleanupEngine.RunAsync(snapshots, true, async (keeperIndex, duplicateIndex) =>
        {
            if (!snapshots[keeperIndex].Matches(keeper) || !snapshots[duplicateIndex].Matches(duplicate)) return DuplicateRemovalResult.Skipped;
            return await database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true)
                ? DuplicateRemovalResult.Removed : DuplicateRemovalResult.Skipped;
        });
        Assert.Equal(new CleanupReport(0, 0, 0, 1, 0), report);
        keeper.Saved = false;
        duplicate.Pinned = false;
        Assert.False(await database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true));
        Assert.Equal(2, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record"));
    }
}
