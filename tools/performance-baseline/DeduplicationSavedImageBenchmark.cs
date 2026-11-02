// DeduplicationSavedImageBenchmark.cs
// Synthetic in-memory database only; no real clipboard, settings, or cache.
using System.Diagnostics;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Test;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;
using Microsoft.Data.Sqlite;

internal static class DeduplicationSavedImageBenchmark
{
    public static void Run()
    {
        foreach (var uniqueCount in new[] { 40, 160 }) Sample(uniqueCount);
    }

    private static void Sample(int uniqueCount)
    {
        var database = new SqliteDatabase("synthetic-image-scale", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var random = new Random(2026);
            var records = new List<ClipboardData>();
            var unloaded = new HashSet<string>();
            for (var i = 0; i < uniqueCount; i++)
            {
                var pixels = new byte[256 * 256 * 4];
                random.NextBytes(pixels);
                var image = BitmapSource.Create(256, 256, 96, 96, PixelFormats.Bgra32, null, pixels, 1024);
                image.Freeze();
                records.Add(Record(image, $"image-{i}"));
                if (i % 20 == 0)
                {
                    var duplicateId = $"duplicate-{i}";
                    records.Add(Record(image, duplicateId));
                    unloaded.Add(duplicateId);
                }
            }
            database.AddRecordsAsync(records, true).GetAwaiter().GetResult();
            var allLoaded = records.Select(record => record.HashId).ToHashSet();
            var partialLoaded = allLoaded.Except(unloaded).ToHashSet();
            var method = typeof(SqliteDatabase).GetMethod("GetCleanupRecordsAsync", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("Synthetic candidate selection unavailable");
            foreach (var scenario in new[] { (Name: "all loaded", Loaded: allLoaded, Expected: 0),
                                             (Name: "duplicates unloaded", Loaded: partialLoaded, Expected: unloaded.Count) })
            {
                var elapsed = new List<double>();
                var waits = new List<double>();
                var overlap = 0;
                for (var i = 0; i < 4; i++)
                {
                    using var started = new ManualResetEventSlim();
                    var timer = Stopwatch.StartNew();
                    var job = Task.Run(async () =>
                    {
                        started.Set();
                        return await (Task<(IReadOnlyList<ClipboardData> Records, int Skipped)>)method.Invoke(database, [true, scenario.Loaded])!;
                    });
                    started.Wait();
                    if (!job.IsCompleted) overlap++;
                    var queue = Stopwatch.StartNew();
                    var references = typeof(SqliteDatabase).GetMethod("GetCacheReferencesAsync")!;
                    ((Task<IReadOnlyList<string>>)references.Invoke(database, null)!).GetAwaiter().GetResult();
                    queue.Stop();
                    var result = job.GetAwaiter().GetResult();
                    timer.Stop();
                    if (result.Records.Count != scenario.Expected || result.Skipped != 0)
                        throw new InvalidOperationException("Synthetic saved image candidate count mismatch");
                    if (i > 0) { elapsed.Add(timer.Elapsed.TotalMilliseconds); waits.Add(queue.Elapsed.TotalMilliseconds); }
                }
                elapsed.Sort(); waits.Sort();
                Console.WriteLine($"dedup saved images {uniqueCount} {scenario.Name}: cleanup ms median/max {elapsed[1]:F2}/{elapsed[2]:F2}; database queue ms median/max {waits[1]:F2}/{waits[2]:F2}; overlap {overlap}/4; materialized {scenario.Expected}");
            }
        }
        finally { database.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static ClipboardData Record(BitmapSource image, string id) => new(image, DataType.Image, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = 0, CreateTime = new DateTime(2026, 1, 1),
        Pinned = false, Saved = true, EncryptKeyMd5 = StringUtils.EncryptKeyMd5
    };
}
