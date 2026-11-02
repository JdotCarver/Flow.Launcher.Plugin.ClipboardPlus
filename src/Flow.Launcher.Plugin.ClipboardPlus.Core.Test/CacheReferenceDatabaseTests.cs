// CacheReferenceDatabaseTests.cs
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

[Collection("Bulk insert isolation")]
public class CacheReferenceDatabaseTests
{
    [Fact]
    public async Task DatabaseReferencesAndAmbiguousPathsPreventCacheDeletion()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = new SqliteDatabase("cache-references", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        await database.InitializeDatabaseAsync();
        var path = Path.Combine(Path.GetTempPath(), "synthetic-cache.png");
        var record = new ClipboardData("synthetic", DataType.PlainText, false)
        {
            HashId = "synthetic", SenderApp = "synthetic", InitScore = 0,
            CreateTime = new DateTime(2026, 1, 1), Pinned = false, Saved = true,
            CachedImagePath = path, EncryptKeyMd5 = StringUtils.EncryptKeyMd5
        };
        await database.AddOneRecordAsync(record, true);
        Assert.Contains(path, await database.GetCacheReferencesAsync());
        var called = false;
        Assert.False(await database.TryDeleteUnreferencedCacheFileAsync(path, () => { called = true; return true; }));
        Assert.False(called);
        await database.DeleteOneRecordAsync(record);
        Assert.True(await database.TryDeleteUnreferencedCacheFileAsync(path, () => { called = true; return true; }));
        Assert.True(called);
        Assert.False(await database.TryDeleteUnreferencedCacheFileAsync("relative.png", () => true));
    }
}
