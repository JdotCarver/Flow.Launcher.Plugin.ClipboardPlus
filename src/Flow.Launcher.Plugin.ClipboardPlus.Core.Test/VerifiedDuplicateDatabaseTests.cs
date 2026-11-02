// VerifiedDuplicateDatabaseTests.cs
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

[Collection("Bulk insert isolation")]
public class VerifiedDuplicateDatabaseTests
{
    private static ClipboardData Record(string id, string content, int minute) => new(content, DataType.PlainText, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = false, Saved = true,
        EncryptKeyMd5 = StringUtils.EncryptKeyMd5
    };

    private static SqliteDatabase CreateDatabase() =>
        new("duplicate-test", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);

    [Fact]
    public async Task DeletesOnlyTheVerifiedDuplicateAndItsAsset()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = CreateDatabase();
        await database.InitializeDatabaseAsync();
        var keeper = Record("keeper", "synthetic", 2);
        var duplicate = Record("duplicate", "synthetic", 1);
        await database.AddRecordsAsync(new[] { keeper, duplicate }, true);
        Assert.True(await database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true));
        Assert.Equal(1, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record"));
        Assert.Equal(1, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM asset"));
        Assert.Equal("keeper", database.Connection.QuerySingle<string>("SELECT hash_id FROM record"));
    }

    [Theory]
    [InlineData("UPDATE record SET pinned = 1 WHERE hash_id = 'duplicate'")]
    [InlineData("UPDATE record SET pinned = 1 WHERE hash_id = 'keeper'")]
    [InlineData("UPDATE asset SET data_b64 = 'changed' WHERE hash_id = 'duplicate'")]
    [InlineData("UPDATE asset SET data_b64 = 'changed' WHERE hash_id = 'keeper'")]
    [InlineData("DELETE FROM record WHERE hash_id = 'keeper'")]
    [InlineData("UPDATE record SET sender_app = 'changed' WHERE hash_id = 'duplicate'")]
    [InlineData("UPDATE record SET cached_image_path = 'changed' WHERE hash_id = 'duplicate'")]
    [InlineData("UPDATE record SET create_time = '2024-01-01T00:00:00' WHERE hash_id = 'keeper'")]
    public async Task ChangedOrMissingRecordsAreNotDeleted(string change)
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = CreateDatabase();
        await database.InitializeDatabaseAsync();
        var keeper = Record("keeper", "synthetic", 2);
        var duplicate = Record("duplicate", "synthetic", 1);
        await database.AddRecordsAsync(new[] { keeper, duplicate }, true);
        database.Connection.Execute(change);
        Assert.False(await database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true));
        Assert.Equal(1, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record WHERE hash_id = 'duplicate'"));
        Assert.Equal(1, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM asset WHERE hash_id = 'duplicate'"));
    }

    [Fact]
    public async Task PreparedDeletionCommitsOnlyWhileBothSnapshotsRemainUnchanged()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = CreateDatabase();
        await database.InitializeDatabaseAsync();
        var keeper = Record("keeper", "synthetic", 2);
        var duplicate = Record("duplicate", "synthetic", 1);
        await database.AddRecordsAsync(new[] { keeper, duplicate }, true);
        var prepared = await database.PrepareDuplicateDeletionAsync(keeper, duplicate, true);
        Assert.NotNull(prepared);
        database.Connection.Execute("UPDATE asset SET data_b64 = 'changed' WHERE hash_id = 'keeper'");
        Assert.False(await database.CommitPreparedDuplicateDeletionAsync(prepared));
        Assert.Equal(2, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record"));
        database.Connection.Execute("UPDATE asset SET data_b64 = 'synthetic' WHERE hash_id = 'keeper'");
        Assert.True(await database.CommitPreparedDuplicateDeletionAsync(prepared));
        Assert.Equal(1, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record"));
    }

    [Fact]
    public async Task PreparedDeletionRejectsPinChangesBetweenVerificationAndCommit()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = CreateDatabase();
        await database.InitializeDatabaseAsync();
        var keeper = Record("keeper", "synthetic", 2);
        var duplicate = Record("duplicate", "synthetic", 1);
        await database.AddRecordsAsync(new[] { keeper, duplicate }, true);
        var prepared = await database.PrepareDuplicateDeletionAsync(keeper, duplicate, true);
        Assert.NotNull(prepared);
        database.Connection.Execute("UPDATE record SET pinned = 1 WHERE hash_id = 'duplicate'");
        Assert.False(await database.CommitPreparedDuplicateDeletionAsync(prepared));
        Assert.Equal(2, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record"));
    }

    [Fact]
    public async Task FailedAssetDeletionRollsBackTheRecordDeletion()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = CreateDatabase();
        await database.InitializeDatabaseAsync();
        var keeper = Record("keeper", "synthetic", 2);
        var duplicate = Record("duplicate", "synthetic", 1);
        await database.AddRecordsAsync(new[] { keeper, duplicate }, true);
        database.Connection.Execute("""
            CREATE TRIGGER fail_duplicate_asset BEFORE DELETE ON asset
            WHEN OLD.hash_id = 'duplicate'
            BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true));
        Assert.Equal(2, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record"));
        Assert.Equal(2, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM asset"));
        database.Connection.Execute("DROP TRIGGER fail_duplicate_asset");
        Assert.True(await database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true));
    }

    [Fact]
    public async Task DoesNotDeleteWhenSnapshotsDifferOrKeeperIsTemporary()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = CreateDatabase();
        await database.InitializeDatabaseAsync();
        var keeper = Record("keeper", "synthetic", 2);
        var duplicate = Record("duplicate", "different", 1);
        await database.AddRecordsAsync(new[] { keeper, duplicate }, true);
        Assert.False(await database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true));
        keeper.Saved = false;
        Assert.False(await database.TryDeleteVerifiedDuplicateAsync(keeper, duplicate, true));
        Assert.Equal(2, database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record"));
    }
}
