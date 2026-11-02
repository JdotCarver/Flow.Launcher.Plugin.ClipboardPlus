// CleanupDatabaseCandidateTests.cs
using Microsoft.Data.Sqlite;
using Dapper;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

[Collection("Bulk insert isolation")]
public class CleanupDatabaseCandidateTests
{
    [Fact]
    public async Task MetadataHashCollisionNeverProvesEquality()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = new SqliteDatabase("synthetic-metadata-collision", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        await database.InitializeDatabaseAsync();
        ClipboardData Text(string text, string id) => new(text, DataType.PlainText, false)
        {
            HashId = id, SenderApp = "synthetic", InitScore = 0, CreateTime = new DateTime(2026, 1, 1),
            Pinned = false, Saved = true, EncryptKeyMd5 = StringUtils.EncryptKeyMd5
        };
        await database.AddRecordsAsync([Text("one", "first"), Text("two", "second")], true);
        await database.Connection.ExecuteAsync("UPDATE record SET data_md5_b64 = 'synthetic-collision';");
        var selected = await database.GetCleanupRecordsAsync(true, new HashSet<string>());
        Assert.Equal(2, selected.Records.Count);
        Assert.Empty(DuplicateRecordPlanner.Plan(selected.Records, true));
    }

    [Fact]
    public async Task OnlyUnloadedPossibleDuplicatePayloadsAreMaterialized()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        await using var database = new SqliteDatabase("synthetic-candidate-selection", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        await database.InitializeDatabaseAsync();
        ClipboardData Image(string id, byte value)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { value, 2, 3, 255 }, 4);
            image.Freeze();
            return new ClipboardData(image, DataType.Image, false)
            {
                HashId = id, SenderApp = "synthetic", InitScore = 0, CreateTime = new DateTime(2026, 1, 1),
                Pinned = false, Saved = true, EncryptKeyMd5 = StringUtils.EncryptKeyMd5
            };
        }
        var unique = Enumerable.Range(1, 20).Select(i => Image($"unique-{i}", (byte)i)).ToArray();
        var first = Image("duplicate-loaded", 100);
        var second = Image("duplicate-unloaded", 100);
        await database.AddRecordsAsync(unique.Concat([first, second]), true);
        var thorough = await database.GetCleanupRecordsAsync(true, new HashSet<string> { first.HashId });
        Assert.Single(thorough.Records);
        Assert.Equal(second.HashId, thorough.Records[0].HashId);
        Assert.Equal(0, thorough.Skipped);
        var bestEffort = await database.GetCleanupRecordsAsync(false, new HashSet<string> { first.HashId });
        Assert.Empty(bestEffort.Records);
        Assert.Equal(1, bestEffort.Skipped);
    }
}
