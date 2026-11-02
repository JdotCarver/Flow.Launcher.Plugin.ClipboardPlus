// ImagePinBenchmark.cs
// Invokes the real plugin pin method with synthetic data and an isolated database.
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

internal static class ImagePinBenchmark
{
    public static void Run()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        var api = DispatchProxy.Create<IPublicAPI, SyntheticHost>();
        Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton(api).BuildServiceProvider());
        var pixels = new byte[1024 * 1024 * 4];
        new Random(123).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var image = BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Bgra32, null, pixels, 1024 * 4);
        image.Freeze();
        string path = Path.Combine(Path.GetTempPath(), $"clipboardplus-pin-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(path, new ClipboardPlus());
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var data = new ClipboardData(image, DataType.Image, false)
            {
                HashId = "synthetic", SenderApp = "synthetic.exe", InitScore = 1,
                CreateTime = new DateTime(2025, 1, 1), EncryptKeyMd5 = string.Empty,
                CachedImagePath = string.Empty, Pinned = false, Saved = true
            };
            database.AddOneRecordAsync(data, true).GetAwaiter().GetResult();
            var pair = new ClipboardDataPair
            {
                ClipboardData = data,
                PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must stay lazy"))
            };
            var plugin = new Plugin();
            SetField(plugin, "ClipboardMonitor", null);
            SetField(plugin, "Settings", new Settings());
            SetField(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
            SetField(plugin, "ScoreHelper", new ScoreHelper(10000));
            SetField(plugin, "Database", database);
            SetField(plugin, "RecordsList", new LinkedList<ClipboardDataPair>([pair]));
            var pin = typeof(Plugin).GetMethod("PinRecordAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var queue = (SemaphoreSlim)typeof(SqliteDatabase).GetField("_queueSemaphore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var durations = new double[5];
            var allocations = new long[5];
            for (int i = -1; i < durations.Length; i++)
            {
                long before = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                ((Task)pin.Invoke(plugin, [pair, false])!).GetAwaiter().GetResult();
                queue.Wait();
                queue.Release();
                timer.Stop();
                long allocated = GC.GetTotalAllocatedBytes(true) - before;
                bool expected = i % 2 != 0;
                if (pair.ClipboardData.Pinned != expected || pair.ClipboardData.DataMd5 != data.DataMd5 ||
                    database.Connection.ExecuteScalar<int>("SELECT pinned FROM record WHERE hash_id='synthetic'") != (expected ? 1 : 0))
                    throw new InvalidOperationException("Synthetic pin validation failed");
                if (i >= 0) { durations[i] = timer.Elapsed.TotalMilliseconds; allocations[i] = allocated; }
            }
            Array.Sort(durations);
            Array.Sort(allocations);
            Console.WriteLine($"actual image pin 1024 noise: ms min/median/max={durations[0]:F2}/{durations[2]:F2}/{durations[4]:F2}; median allocated={allocations[2] / 1048576.0:F2} MiB");
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void SetField(Plugin plugin, string name, object? value) =>
        (typeof(Plugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Plugin test seam changed")).SetValue(plugin, value);
}
