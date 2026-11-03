// Program.cs
// Synthetic, opt-in microbenchmarks. Never reads the user's clipboard or database.
global using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Test;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;

internal static class Program
{
    private const int Repetitions = 5;
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--local-pin-probe")
        {
            LocalPinProbe.RunAsync().GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 1 && args[0] == "--gc-ab-only")
        {
            RuntimeAbBenchmark.RunGc();
            return;
        }
        if (args.Length == 1 && (args[0] == "--startup-image-eager" || args[0] == "--startup-image-metadata"))
        {
            RuntimeAbBenchmark.RunImageStartup(args[0] == "--startup-image-metadata");
            return;
        }
        if (args.Length == 1 && args[0] == "--image-pin-only")
        {
            ImagePinBenchmark.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--image-capture-only")
        {
            ImageCaptureBenchmark.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--bulk-only")
        {
            BulkMethodBenchmark.Run();
            return;
        }
        Console.WriteLine("Synthetic baseline; Release build; five measured repetitions after one warmup.");
        Console.WriteLine($"Runtime: {Environment.Version}; logical processors: {Environment.ProcessorCount}");
        Console.WriteLine("Allocations are process-wide managed bytes; working set is a process snapshot, not Flow Launcher memory.");
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());

        foreach (int count in new[] { 100, 1000, 10000 })
        {
            Measure($"text create and display, {count} records", () =>
            {
                var records = CreateTextRecords(count);
                foreach (var record in records)
                {
                    var item = record;
                    if (item.GetText(Culture).Length == 0 || item.GetTitle(Culture).Length == 0)
                        throw new InvalidOperationException("Text display regression");
                    _ = item.GetSubtitle(Culture);
                }
                GC.KeepAlive(records);
            });
        }

        // Compare compressible and high-entropy synthetic images; real screenshots vary.
        foreach (bool noisy in new[] { false, true })
        {
            var sample = CreateImage(512, noisy, 123);
            Measure($"image hashing only, 512x512, {(noisy ? "noise" : "solid")}", () =>
            {
                var record = CreateRecord(sample, DataType.Image, 1);
                if (string.IsNullOrEmpty(record.DataMd5)) throw new InvalidOperationException("Image hash missing");
            });
        }

        // The same bitmap is used throughout: no clipboard or personal image is accessed.
        int width = 1024, height = 1024;
        var pixels = new byte[width * height * 4];
        new Random(42).NextBytes(pixels);
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        Measure("image construct and serialize, 1024x1024", () =>
        {
            var record = CreateRecord(image, DataType.Image, 1);
            if (string.IsNullOrEmpty(record.DataToString(false)))
                throw new InvalidOperationException("Image serialization regression");
        });

        Measure("two forced collections and finalizer wait, isolated", () =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });

        foreach (int count in new[] { 100, 1000 })
        {
            var records = CreateTextRecords(count);
            Measure($"SQLite disk bulk insert and read, {count} records", () =>
                DatabaseRoundTrip(records));
        }

        MeasureDatabaseRestore("1000 text records", CreateTextRecords(1000));
        var imageRecords = Enumerable.Range(0, 10)
            .Select(i => CreateRecord(CreateImage(256, true, i), DataType.Image, i)).ToArray();
        MeasureDatabaseRestore("10 noisy 256x256 images", imageRecords);
        MeasureRetainedTextMemory();
        PluginScenarios.Run();
        DeduplicationPlannerBenchmark.Run();
        DeduplicationDatabaseBenchmark.Run();
        DuplicatePluginIntegration.Run();
        DeduplicationContentionBenchmark.Run();
        DeduplicationSavedImageBenchmark.Run();
        SqliteTransactionExperiment.Run();
        ImageExperiment.Run();
        ImagePinInvestigation.Run();
    }

    private static BitmapSource CreateImage(int size, bool noisy, int seed)
    {
        var pixels = new byte[size * size * 4];
        if (noisy) new Random(seed).NextBytes(pixels);
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void MeasureDatabaseRestore(string label, ClipboardData[] records)
    {
        string path = Path.Combine(Path.GetTempPath(), $"clipboardplus-baseline-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(path, new ClipboardPlus());
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            database.AddRecordsAsync(records, false).GetAwaiter().GetResult();
            database.CloseAsync().GetAwaiter().GetResult();
            Measure($"SQLite reopen and materialize {label}", () =>
            {
                database.OpenAsync().GetAwaiter().GetResult();
                var loaded = database.GetAllRecordsAsync(true).GetAwaiter().GetResult();
                if (loaded.Count != records.Length || loaded.Select(x => x.HashId).Distinct().Count() != records.Length)
                    throw new InvalidOperationException("Database restore count or identity regression");
                GC.KeepAlive(loaded);
                database.CloseAsync().GetAwaiter().GetResult();
            });
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void MeasureRetainedTextMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        var records = CreateTextRecords(10000);
        long after = GC.GetTotalMemory(true);
        Console.WriteLine($"10000 live synthetic text records: managed heap delta={(after - before) / 1048576.0:F2} MiB (after full GC; excludes native memory)");
        GC.KeepAlive(records);
    }

    private static ClipboardData[] CreateTextRecords(int count) => Enumerable.Range(0, count)
        .Select(i => CreateRecord($"Synthetic clipboard record {i}: searchable text for performance testing.", DataType.PlainText, i))
        .ToArray();

    private static ClipboardData CreateRecord(object content, DataType type, int index) => new(content, type, false)
    {
        HashId = index.ToString(CultureInfo.InvariantCulture),
        SenderApp = "synthetic.exe",
        InitScore = index,
        CreateTime = new DateTime(2025, 1, 1).AddSeconds(index),
        EncryptKeyMd5 = StringUtils.EncryptKeyMd5,
        CachedImagePath = string.Empty,
        Pinned = false,
        Saved = true
    };

    private static void DatabaseRoundTrip(ClipboardData[] records)
    {
        string path = Path.Combine(Path.GetTempPath(), $"clipboardplus-baseline-{Guid.NewGuid():N}.db");
        var owner = new ClipboardPlus();
        var database = new SqliteDatabase(path, owner);
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            database.AddRecordsAsync(records, true).GetAwaiter().GetResult();
            var loaded = database.GetAllRecordsAsync(true).GetAwaiter().GetResult();
            if (loaded.Count != records.Length || loaded.Select(x => x.HashId).Distinct().Count() != records.Length)
                throw new InvalidOperationException("Database round-trip count or identity regression");
            GC.KeepAlive(loaded);
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static void Measure(string name, Action action)
    {
        action(); // warm up JIT and the workload
        var durations = new double[Repetitions];
        var allocated = new long[Repetitions];
        for (int i = 0; i < Repetitions; i++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            action();
            timer.Stop();
            durations[i] = timer.Elapsed.TotalMilliseconds;
            allocated[i] = GC.GetTotalAllocatedBytes(true) - before;
        }
        Array.Sort(durations);
        Array.Sort(allocated);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        Console.WriteLine($"{name}: ms min/median/max={durations[0]:F2}/{durations[2]:F2}/{durations[4]:F2}; " +
            $"median allocated={allocated[2] / 1048576.0:F2} MiB; working set snapshot={process.WorkingSet64 / 1048576.0:F2} MiB");
    }
}
