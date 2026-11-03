// RuntimeAbBenchmark.cs
// Synthetic-only component comparisons; never access real clipboard data or settings.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.DependencyInjection;
using Dapper;
using Flow.Launcher.Plugin;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Test;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Plugin = Flow.Launcher.Plugin.ClipboardPlus.ClipboardPlus;
using Settings = Flow.Launcher.Plugin.ClipboardPlus.Settings;

internal static class RuntimeAbBenchmark
{
    private const int Samples = 5;

    public static void RunGc()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        var api = DispatchProxy.Create<IPublicAPI, SyntheticHost>();
        Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton(api).BuildServiceProvider());
        var database = new SqliteDatabase("synthetic-gc", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var plugin = new Plugin();
            SetField(plugin, "ClipboardMonitor", null);
            SetField(plugin, "Settings", new Settings { MaxRecords = 20000, AutomaticCleanup = false });
            SetField(plugin, "Context", new PluginInitContext(null, api));
            SetField(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
            SetField(plugin, "ScoreHelper", new ScoreHelper(10000));
            SetField(plugin, "Database", database);
            ClipboardData Record(int i, bool saved = false) => new($"synthetic gc record {i}", DataType.PlainText, false)
            {
                HashId = i.ToString(CultureInfo.InvariantCulture), SenderApp = "synthetic", InitScore = i,
                CreateTime = new DateTime(2025, 1, 1).AddSeconds(i), Pinned = false, Saved = saved
            };
            var pinData = Record(0, true);
            database.AddOneRecordAsync(pinData, true).GetAwaiter().GetResult();
            var pinPair = new ClipboardDataPair { ClipboardData = pinData,
                PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must remain lazy")) };
            var list = new LinkedList<ClipboardDataPair>();
            list.AddFirst(pinPair);
            for (var i = 1; i <= 1000; i++)
            {
                var data = Record(i);
                list.AddFirst(new ClipboardDataPair { ClipboardData = data,
                    PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must remain lazy")) });
            }
            SetField(plugin, "RecordsList", list);
            // A fixed live synthetic heap exposes collection cost without touching user data.
            var heap = new byte[64 * 1024 * 1024];
            for (int i = 0; i < heap.Length; i += 4096) heap[i] = 1;
            var add = typeof(Plugin).GetMethod("AddClipboardDataItem", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(ClipboardData)], null)!;
            var pin = typeof(Plugin).GetMethod("PinRecordAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var wait = typeof(SqliteDatabase).GetMethod("WaitTaskQueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var recordLock = (SemaphoreSlim)typeof(Plugin).GetField("RecordsLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            var captures = new double[Samples]; var pins = new double[Samples];
            var next = 1001;
            for (int sample = -1; sample < Samples; sample++)
            {
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 20; i++)
                {
                    var data = Record(next++);
                    recordLock.Wait();
                    try { add.Invoke(plugin, [data]); }
                    finally { recordLock.Release(); }
                }
                timer.Stop();
                var captureMs = timer.Elapsed.TotalMilliseconds / 20;
                timer.Restart();
                for (int i = 0; i < 10; i++) ((Task)pin.Invoke(plugin, [pinPair, false])!).GetAwaiter().GetResult();
                ((Task)wait.Invoke(database, null)!).GetAwaiter().GetResult();
                timer.Stop();
                if (pinPair.ClipboardData.Pinned || list.Count != next ||
                    database.Connection.ExecuteScalar<int>("SELECT pinned FROM record WHERE hash_id = '0'") != 0)
                    throw new InvalidOperationException("Synthetic capture or pin validation failed");
                if (sample >= 0) { captures[sample] = captureMs; pins[sample] = timer.Elapsed.TotalMilliseconds / 10; }
            }
            Array.Sort(captures); Array.Sort(pins);
            using var process = Process.GetCurrentProcess(); process.Refresh();
            Console.WriteLine($"runtime ab gc capture ms min median max {captures[0]:F3} {captures[2]:F3} {captures[4]:F3}");
            Console.WriteLine($"runtime ab gc pin ms min median max {pins[0]:F3} {pins[2]:F3} {pins[4]:F3}");
            Console.WriteLine($"runtime ab gc final managed working private MiB {GC.GetTotalMemory(false) / 1048576.0:F2} {process.WorkingSet64 / 1048576.0:F2} {process.PrivateMemorySize64 / 1048576.0:F2}");
            GC.KeepAlive(heap);
        }
        finally { database.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public static void RunImageStartup(bool metadataOnly)
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        var path = Path.Combine(Path.GetTempPath(), $"clipboardplus-startup-ab-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(path, new ClipboardPlus());
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var records = new List<ClipboardData>();
            for (var i = 0; i < 24; i++)
            {
                var pixels = new byte[512 * 512 * 4]; new Random(i).NextBytes(pixels);
                for (var j = 3; j < pixels.Length; j += 4) pixels[j] = 255;
                var image = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 512 * 4); image.Freeze();
                records.Add(new ClipboardData(image, DataType.Image, false) {
                    HashId = i.ToString(CultureInfo.InvariantCulture), SenderApp = "synthetic", InitScore = i,
                    CreateTime = new DateTime(2025, 1, 1).AddSeconds(i), Pinned = false, Saved = true });
            }
            var hashes = records.ToDictionary(item => item.HashId, item => item.DataMd5);
            database.AddRecordsAsync(records, true).GetAwaiter().GetResult();
            records.Clear();
            var startup = new double[Samples]; var first = new double[Samples];
            var allocated = new double[Samples]; var working = new double[Samples]; var privateBytes = new double[Samples];
            for (int sample = -1; sample < Samples; sample++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                using var process = Process.GetCurrentProcess(); process.Refresh();
                var baseWorking = process.WorkingSet64; var basePrivate = process.PrivateMemorySize64;
                var baseAlloc = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                List<ClipboardData>? eager = null;
                List<Record>? metadata = null;
                if (metadataOnly)
                    metadata = database.Connection.Query<Record>("SELECT r.hash_id AS HashId, r.data_type AS DataType, r.create_time AS CreateTime, r.pinned AS Pinned, r.sender_app AS SenderApp, r.data_md5_b64 AS DataMd5B64, r.encrypt_data AS EncryptData, r.cached_image_path AS CachedImagePath, r.encrypt_key_md5 AS EncryptKeyMd5 FROM record r ORDER BY r.datetime_score ASC").ToList();
                else eager = database.GetAllRecordsAsync(true).GetAwaiter().GetResult();
                timer.Stop();
                var startupMs = timer.Elapsed.TotalMilliseconds;
                var allocation = (GC.GetTotalAllocatedBytes(true) - baseAlloc) / 1048576.0;
                process.Refresh(); var workingDelta = (process.WorkingSet64 - baseWorking) / 1048576.0;
                var privateDelta = (process.PrivateMemorySize64 - basePrivate) / 1048576.0;
                if ((metadata?.Count ?? eager?.Count) != hashes.Count) throw new InvalidOperationException("Synthetic startup count changed");
                timer.Restart();
                ClipboardData selected;
                if (metadataOnly)
                {
                    var row = database.Connection.QuerySingle<Record>("SELECT r.hash_id AS HashId, a.data_b64 AS DataMd5B64, r.data_type AS DataType, r.encrypt_data AS EncryptData, r.create_time AS CreateTime, r.sender_app AS SenderApp, r.pinned AS Pinned, r.cached_image_path AS CachedImagePath, r.encrypt_key_md5 AS EncryptKeyMd5, a.plain_text_b64 AS PlainText FROM record r JOIN asset a ON a.hash_id=r.hash_id WHERE r.hash_id=@Id", new { Id = metadata![0].HashId });
                    selected = ClipboardData.FromRecord(row, 0);
                }
                else selected = eager![0];
                timer.Stop();
                if (selected.Data is not BitmapSource bitmap || bitmap.PixelWidth != 512 || bitmap.PixelHeight != 512 || selected.DataMd5 != hashes[selected.HashId])
                    throw new InvalidOperationException("Synthetic first image did not round trip");
                if (sample >= 0) { startup[sample] = startupMs; first[sample] = timer.Elapsed.TotalMilliseconds; allocated[sample] = allocation; working[sample] = workingDelta; privateBytes[sample] = privateDelta; }
                GC.KeepAlive(eager); GC.KeepAlive(metadata);
            }
            Array.Sort(startup); Array.Sort(first); Array.Sort(allocated); Array.Sort(working); Array.Sort(privateBytes);
            Console.WriteLine($"runtime ab image load ms min median max {startup[0]:F2} {startup[2]:F2} {startup[4]:F2}");
            Console.WriteLine($"runtime ab image first ms min median max {first[0]:F2} {first[2]:F2} {first[4]:F2}");
            Console.WriteLine($"runtime ab image median allocated working private delta MiB {allocated[2]:F2} {working[2]:F2} {privateBytes[2]:F2}");
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult(); SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void SetField(Plugin plugin, string name, object? value) =>
        (typeof(Plugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Synthetic host seam changed")).SetValue(plugin, value);
}
