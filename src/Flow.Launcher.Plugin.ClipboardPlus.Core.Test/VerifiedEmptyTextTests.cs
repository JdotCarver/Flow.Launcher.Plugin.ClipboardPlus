// VerifiedEmptyTextTests.cs
using Dapper;
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
            Make("whitespace", DataType.PlainText, " ", ""), Make("rich-plain", DataType.RichText, "", "content"), Make("excluded", DataType.PlainText, "", "") })
            await database.AddOneRecordAsync(value, true);
        await database.AddOneRecordAsync(Make("damaged", DataType.PlainText, "", ""), true);
        await database.AddOneRecordAsync(Make("bad-plain", DataType.RichText, "", ""), true);
        await database.AddOneRecordAsync(Make("bad-hash-only", DataType.PlainText, "nonempty", ""), true);
        await database.AddOneRecordAsync(Make("bad-flag", DataType.PlainText, "", ""), true);
        await database.Connection.ExecuteAsync("UPDATE record SET data_md5_b64 = @Hash WHERE hash_id = 'damaged'", new { Hash = StringUtils.GetMd5("nonempty") });
        await database.Connection.ExecuteAsync("UPDATE record SET plain_text = 'nonempty' WHERE hash_id = 'bad-plain'");
        await database.Connection.ExecuteAsync("UPDATE record SET data_md5_b64 = @Hash WHERE hash_id = 'bad-hash-only'", new { Hash = StringUtils.GetMd5("") });
        await database.Connection.ExecuteAsync("UPDATE record SET encrypt_data = 2 WHERE hash_id = 'bad-flag'");
        var removed = await database.DeleteVerifiedEmptyTextAsync(["excluded"]);
        Assert.Equal(new[] { "empty", "rich" }.Order(), removed.Order());
        Assert.Equal(8, (await database.GetAllRecordsAsync(false)).Count);
        Assert.Equal(8, await database.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset"));
        Assert.Equal(1, await database.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM record WHERE hash_id = 'damaged'"));
        Assert.Equal(1, await database.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM asset WHERE hash_id = 'damaged'"));
    }
}
