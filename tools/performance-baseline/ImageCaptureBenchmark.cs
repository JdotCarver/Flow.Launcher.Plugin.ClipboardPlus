// ImageCaptureBenchmark.cs
// Invokes the real monitor event handler with generated data and an isolated database.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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

internal static class ImageCaptureBenchmark
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
        Sample(image);
        var times = Enumerable.Range(0, 5).Select(_ => Sample(image)).Order().ToArray();
        Console.WriteLine($"actual image capture save 1024 noise: ms min/median/max={times[0]:F2}/{times[2]:F2}/{times[4]:F2}");
    }

    private static double Sample(BitmapSource image)
    {
        string path = Path.Combine(Path.GetTempPath(), $"clipboardplus-capture-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(path, new ClipboardPlus());
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var plugin = new Plugin();
            SetField(plugin, "ClipboardMonitor", null);
            SetField(plugin, "Settings", new Settings { KeepImages = true, CacheImages = false });
            SetField(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
            SetField(plugin, "ScoreHelper", new ScoreHelper(10000));
            SetField(plugin, "Database", database);
            var sender = DispatchProxy.Create<IClipboardMonitor, SyntheticMonitor>();
            var args = new ClipboardChangedEventArgs(image, DataType.Image, SourceApplication.NULL);
            var handler = typeof(Plugin).GetMethod("ClipboardMonitor_OnClipboardChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var recordsLock = (SemaphoreSlim)typeof(Plugin).GetField("RecordsLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            var queueGate = (SemaphoreSlim)typeof(SqliteDatabase).GetField("_queueSemaphore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var timer = Stopwatch.StartNew();
            handler.Invoke(plugin, [sender, args]);
            // The event acquires this lock before starting the database write.
            recordsLock.Wait();
            recordsLock.Release();
            queueGate.Wait();
            queueGate.Release();
            timer.Stop();
            if (database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM record") != 1 ||
                database.Connection.ExecuteScalar<int>("SELECT COUNT(*) FROM asset") != 1)
                throw new InvalidOperationException("Synthetic captured image was not saved");
            var stored = database.Connection.QuerySingle<string>("SELECT data_b64 FROM asset");
            if (stored != image.ToBase64()) throw new InvalidOperationException("Synthetic image storage mismatch");
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

    private static void SetField(Plugin plugin, string name, object? value) =>
        (typeof(Plugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Plugin test seam changed")).SetValue(plugin, value);
}

public class SyntheticMonitor : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_ClipboardText" or "get_ClipboardRtfText" => string.Empty,
        _ => throw new InvalidOperationException("Unexpected synthetic monitor operation")
    };
}
