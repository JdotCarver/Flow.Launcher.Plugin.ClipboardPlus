// BulkMethodBenchmark.cs
// Measures the real bulk-insert method against synthetic temporary databases.
using System.Diagnostics;
using System.Globalization;
using Dapper;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Test;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;
using Microsoft.Data.Sqlite;

internal static class BulkMethodBenchmark
{
    public static void Run()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        var records = Enumerable.Range(0, 500).Select(i => new ClipboardData($"synthetic record {i}", DataType.PlainText, false)
        {
            HashId = i.ToString(CultureInfo.InvariantCulture),
            SenderApp = "synthetic.exe",
            InitScore = i,
            CreateTime = new DateTime(2025, 1, 1).AddSeconds(i),
            EncryptKeyMd5 = string.Empty,
            CachedImagePath = string.Empty,
            Pinned = false,
            Saved = true
        }).ToArray();
        Sample(records); // warmup
        var times = Enumerable.Range(0, 5).Select(_ => Sample(records)).Order().ToArray();
        Console.WriteLine($"actual bulk insert 500 records: ms min/median/max={times[0]:F2}/{times[2]:F2}/{times[4]:F2}");
    }

    private static double Sample(ClipboardData[] records)
    {
        string path = Path.Combine(Path.GetTempPath(), $"clipboardplus-method-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(path, new ClipboardPlus());
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var timer = Stopwatch.StartNew();
            database.AddRecordsAsync(records, true).GetAwaiter().GetResult();
            timer.Stop();
            if (database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record") != records.Length ||
                database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM asset") != records.Length ||
                database.Connection.QuerySingle<string>("SELECT data_b64 FROM asset WHERE hash_id='0'") != "synthetic record 0")
                throw new InvalidOperationException("Synthetic bulk method verification failed");
            return timer.Elapsed.TotalMilliseconds;
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
