// DeduplicationDatabaseBenchmark.cs
// Isolated in-memory SQLite only; no user history, settings, cache or clipboard.
using System.Diagnostics;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Test;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;
using Microsoft.Data.Sqlite;

internal static class DeduplicationDatabaseBenchmark
{
    public static void Run()
    {
        foreach (var sample in new[] { (Length: 4096, Pairs: 50), (Length: 65536, Pairs: 20), (Length: 1048576, Pairs: 4) })
        {
            var samples = new double[3];
            for (int run = 0; run < 4; run++)
            {
                var elapsed = Sample(sample.Length, sample.Pairs);
                if (run != 0) samples[run - 1] = elapsed;
            }
            Array.Sort(samples);
            Console.WriteLine($"dedup database {sample.Pairs} pairs text length {sample.Length}: ms min/median/max={samples[0]:F2}/{samples[1]:F2}/{samples[2]:F2}");
        }
    }

    private static double Sample(int length, int pairs)
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        var database = new SqliteDatabase("dedup-benchmark", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var data = Enumerable.Range(0, pairs).Select(i =>
            {
                var text = new string((char)('a' + i % 26), length) + i;
                return (Keeper: Record(text, $"keeper-{i}", 2), Duplicate: Record(text, $"duplicate-{i}", 1));
            }).ToArray();
            database.AddRecordsAsync(data.SelectMany(pair => new[] { pair.Keeper, pair.Duplicate }), true).GetAwaiter().GetResult();
            var timer = Stopwatch.StartNew();
            var delete = typeof(SqliteDatabase).GetMethod("TryDeleteVerifiedDuplicateAsync")
                ?? throw new InvalidOperationException("Duplicate verification is unavailable");
            foreach (var pair in data)
            {
                if (!((Task<bool>)delete.Invoke(database, new object[] { pair.Keeper, pair.Duplicate, true })!).GetAwaiter().GetResult())
                    throw new InvalidOperationException("Synthetic duplicate revalidation failed");
            }
            timer.Stop();
            return timer.Elapsed.TotalMilliseconds;
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static ClipboardData Record(string text, string id, int minute) => new(text, DataType.PlainText, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = false, Saved = true,
        EncryptKeyMd5 = StringUtils.EncryptKeyMd5
    };
}
