// VerifiedEmptyTextTests.cs
using Microsoft.Data.Sqlite;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class VerifiedEmptyTextTests
{
    [Fact]
    public async Task OnlyUnpinnedVerifiedEmptyTextIsDeleted()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = new SqliteDatabase("synthetic-empty", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        await database.InitializeDatabaseAsync();
        ClipboardData Make(string id, DataType type, string content, string plain, bool pinned = false) => new(content, type, false)
        {
            HashId = id, SenderApp = "synthetic", InitScore = 0, CreateTime = DateTime.Now,
            Pinned = pinned, Saved = true, PlainText = plain
        };
        foreach (var value in new[] { Make("empty", DataType.PlainText, "", ""),
            Make("rich", DataType.RichText, "", ""), Make("pinned", DataType.PlainText, "", "", true),
            Make("whitespace", DataType.PlainText, " ", ""), Make("rich-plain", DataType.RichText, "", "content") })
            await database.AddOneRecordAsync(value, true);
        var removed = await database.DeleteVerifiedEmptyTextAsync(["excluded"]);
        Assert.Equal(new[] { "empty", "rich" }.Order(), removed.Order());
        Assert.Equal(4, (await database.GetAllRecordsAsync(false)).Count);
    }
}
