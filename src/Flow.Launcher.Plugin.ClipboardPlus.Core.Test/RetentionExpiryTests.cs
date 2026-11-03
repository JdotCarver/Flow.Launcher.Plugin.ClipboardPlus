// RetentionExpiryTests.cs
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
                HashId = id, SenderApp = "synthetic", CreateTime = age, Pinned = pinned, Saved = true
            };
            await db.AddOneRecordAsync(Make("pinned", true, DateTime.Now.AddDays(-3)), true);
            await db.AddOneRecordAsync(Make("old", false, DateTime.Now.AddDays(-3)), true);
            await db.AddOneRecordAsync(Make("recent", false, DateTime.Now), true);
            var durations = new[] { ((int)DataType.PlainText, 24) };
            var removed = await db.ExpireRecordsAsync(durations, true);
            Assert.Equal(new[] { "old" }, removed);
            Assert.Equal(2, (await db.GetAllRecordsAsync(false)).Count);
            removed = await db.ExpireRecordsAsync(durations, false);
            Assert.Equal(new[] { "pinned" }, removed);
            Assert.Single(await db.GetAllRecordsAsync(false));
        }
        finally { await db.DisposeAsync(); }
    }
}
