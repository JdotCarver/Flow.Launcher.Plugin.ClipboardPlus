// DeduplicationContentionBenchmark.cs
// Synthetic plugin records and private in-memory SQLite; never starts the monitor.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.DependencyInjection;
using Flow.Launcher.Plugin;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Test;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;
using Microsoft.Data.Sqlite;
using Plugin = Flow.Launcher.Plugin.ClipboardPlus.ClipboardPlus;
using Settings = Flow.Launcher.Plugin.ClipboardPlus.Settings;

internal static class DeduplicationContentionBenchmark
{
    public static void Run()
    {
        foreach (var work in new[] { CleanupWork.AutomaticBestEffort, CleanupWork.AutomaticThorough })
            Sample(work);
    }

    private static void Sample(CleanupWork work)
    {
        var database = new SqliteDatabase($"synthetic-contention-{work}", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var plugin = new Plugin();
            Set(plugin, "ClipboardMonitor", null);
            Set(plugin, "Context", new PluginInitContext(null, Ioc.Default.GetRequiredService<IPublicAPI>()));
            Set(plugin, "Settings", new Settings { AutomaticCleanup = true, MaxRecords = 10000 });
            Set(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
            Set(plugin, "ScoreHelper", new ScoreHelper(10000));
            Set(plugin, "Database", database);
            var records = new List<ClipboardData>();
            for (int i = 0; i < 250; i++)
            {
                var text = new string((char)('a' + i % 26), 4090) + i.ToString("D4");
                records.Add(Record(text, DataType.PlainText, $"a-{i}", 2));
                records.Add(Record(text, DataType.PlainText, $"b-{i}", 1));
            }
            var pixels = new byte[1024 * 1024 * 4];
            new Random(1234).NextBytes(pixels);
            var image = BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Bgra32, null, pixels, 4096);
            image.Freeze();
            for (int i = 0; i < 4; i++) records.Add(Record(image, DataType.Image, $"image-{i}", i));
            database.AddRecordsAsync(records, true).GetAwaiter().GetResult();
            var list = new LinkedList<ClipboardDataPair>(records.Select(data => new ClipboardDataPair
            {
                ClipboardData = data,
                PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must stay lazy"))
            }));
            Set(plugin, "RecordsList", list);
            var query = new Query { ActionKeyword = "cbp", SearchTerms = ["unmatched"] };
            typeof(Query).GetProperty("Search")!.SetValue(query, "unmatched");
            var cleanup = typeof(Plugin).GetMethod("RunCleanupWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var gate = (SemaphoreSlim)typeof(Plugin).GetField("RecordsLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            var add = typeof(Plugin).GetMethod("AddClipboardDataItem", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(ClipboardData)], null)!;
            var timer = Stopwatch.StartNew();
            var job = Task.Run(async () => await (Task<CleanupReport>)cleanup.Invoke(plugin, [work])!);
            var capture = new List<double>();
            var queries = new List<double>();
            int overlapping = 0;
            for (int i = 0; i < 20; i++)
            {
                if (!job.IsCompleted) overlapping++;
                var watch = Stopwatch.StartNew();
                gate.Wait();
                try
                {
                    var item = plugin.GetClipboardDataItem($"synthetic capture {i}", DataType.PlainText,
                        $"capture-{i}", new DateTime(2026, 2, 1), SourceApplication.NULL, "", "");
                    add.Invoke(plugin, [item]);
                }
                finally { gate.Release(); }
                capture.Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart();
                plugin.QueryAsync(query, CancellationToken.None).GetAwaiter().GetResult();
                queries.Add(watch.Elapsed.TotalMilliseconds);
            }
            var report = job.GetAwaiter().GetResult();
            timer.Stop();
            if (report.Errors != 0 || report.RecordsRemoved < 200)
                throw new InvalidOperationException("Synthetic contention cleanup failed");
            capture.Sort(); queries.Sort();
            Console.WriteLine($"dedup integrated {work}: total with 20 captures+queries {timer.Elapsed.TotalMilliseconds:F2} ms; overlap {overlapping}/20; capture ms median/max {capture[10]:F2}/{capture[^1]:F2}; query ms median/max {queries[10]:F2}/{queries[^1]:F2}; removed {report.RecordsRemoved}, skipped {report.Skipped}");
        }
        finally { database.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static ClipboardData Record(object content, DataType type, string id, int minute) => new(content, type, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = minute, CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute),
        Pinned = false, Saved = true, EncryptKeyMd5 = StringUtils.EncryptKeyMd5
    };

    private static void Set(Plugin plugin, string name, object? value) =>
        typeof(Plugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, value);
}
