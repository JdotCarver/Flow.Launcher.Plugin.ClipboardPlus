// RetentionExpiryTests.cs
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class RetentionExpiryTests
{
    [Fact]
    public async Task ExpiryProtectsAllPinsWhenRequestedAndUsesTypeDurations()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        var db = new SqliteDatabase("synthetic-expiry", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        try
        {
            await db.InitializeDatabaseAsync();
            ClipboardData Make(string id, bool pinned, DateTime age) => new("synthetic", DataType.PlainText, false)
            {
                HashId = id, SenderApp = "synthetic", InitScore = 0, CreateTime = age, Pinned = pinned, Saved = true
            };
            await db.AddOneRecordAsync(Make("pinned", true, DateTime.Now.AddDays(-3)), true);
            await db.AddOneRecordAsync(Make("old", false, DateTime.Now.AddDays(-3)), true);
            await db.AddOneRecordAsync(Make("recent", false, DateTime.Now), true);
            await db.Connection.ExecuteAsync("INSERT INTO asset (hash_id, data_b64) VALUES ('unrelated-orphan', 'preserve')");
            var durations = new[] { ((int)DataType.PlainText, 24) };
            var removed = await db.ExpireRecordsAsync(durations, true);
            Assert.Equal(new[] { "old" }, removed);
            Assert.Equal(2, (await db.GetAllRecordsAsync(false)).Count);
            Assert.Equal(0, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset WHERE hash_id = 'old'"));
            Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset WHERE hash_id = 'pinned'"));
            var replacement = new ClipboardData("replacement", DataType.PlainText, false)
            {
                HashId = "old", SenderApp = "synthetic", InitScore = 0, CreateTime = DateTime.Now, Pinned = false, Saved = true
            };
            await db.AddOneRecordAsync(replacement, true);
            Assert.Equal("replacement", await db.Connection.ExecuteScalarAsync<string>("SELECT data_b64 FROM asset WHERE hash_id = 'old'"));
            removed = await db.ExpireRecordsAsync(durations, false);
            Assert.Equal(new[] { "pinned" }, removed);
            Assert.Equal(2, (await db.GetAllRecordsAsync(false)).Count);
            Assert.Equal(0, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset WHERE hash_id = 'pinned'"));
            Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset WHERE hash_id = 'unrelated-orphan'"));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ExpiryReportsExactlyTheDeletedRowsAcrossAClockBoundary()
    {
        await using var db = new SqliteDatabase("synthetic-expiry-boundary", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        await db.InitializeDatabaseAsync();
        var record = new ClipboardData("synthetic", DataType.PlainText, false)
        {
            HashId = "boundary", SenderApp = "synthetic", InitScore = 0,
            CreateTime = DateTime.Now, Pinned = false, Saved = true
        };
        await db.AddOneRecordAsync(record, true);
        // The synthetic clock advances a second on each use of 'now'. The
        // record is exactly at the limit on the first call, and expires on the next.
        var clockReads = 0;
        db.Connection.CreateFunction<string, string, string>("strftime", (format, value) =>
            value == "now" ? (10000 + clockReads++).ToString() : "6400");
        var durations = new[] { ((int)DataType.PlainText, 1) };
        var first = await db.ExpireRecordsAsync(durations, false);
        Assert.Empty(first);
        Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM record"));
        Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset"));
        var second = await db.ExpireRecordsAsync(durations, false);
        Assert.Equal(new[] { "boundary" }, second);
        Assert.Equal(0, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM record"));
        Assert.Equal(0, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset"));
    }

    [Fact]
    public async Task ExpiryRollsBackRecordsWhenAssetDeletionFails()
    {
        await using var db = new SqliteDatabase("synthetic-expiry-rollback", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        await db.InitializeDatabaseAsync();
        var record = new ClipboardData("synthetic", DataType.PlainText, false)
        {
            HashId = "old", SenderApp = "synthetic", InitScore = 0, CreateTime = DateTime.Now.AddDays(-3), Pinned = false, Saved = true
        };
        await db.AddOneRecordAsync(record, true);
        await db.Connection.ExecuteAsync("CREATE TRIGGER block_asset_delete BEFORE DELETE ON asset BEGIN SELECT RAISE(ABORT, 'blocked'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => db.ExpireRecordsAsync([((int)DataType.PlainText, 24)], false));
        Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM record"));
        Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset"));
    }
}
