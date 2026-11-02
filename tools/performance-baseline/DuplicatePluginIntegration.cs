// DuplicatePluginIntegration.cs
// Exercises the plugin cleanup against synthetic records and in-memory SQLite.
using System.Globalization;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using Flow.Launcher.Plugin;
using Flow.Launcher.Plugin.ClipboardPlus.Panels.ViewModels;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Test;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;
using Microsoft.Data.Sqlite;
using Plugin = Flow.Launcher.Plugin.ClipboardPlus.ClipboardPlus;
using Settings = Flow.Launcher.Plugin.ClipboardPlus.Settings;

internal static class DuplicatePluginIntegration
{
    public static void Run()
    {
        StringUtils.InitEncryptKey(StringUtils.GenerateEncryptKey());
        var database = new SqliteDatabase("synthetic-plugin-cleanup", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var records = new[] { Record("keeper", 3, true), Record("saved", 2, true), Record("temporary", 1, false), Record("pinned", 4, true, true) };
            database.AddRecordsAsync(records.Where(record => record.Saved).Append(Record("unloaded-saved", 0, true)), true).GetAwaiter().GetResult();
            var list = new LinkedList<ClipboardDataPair>(records.Select(record => new ClipboardDataPair
            {
                ClipboardData = record,
                PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must stay lazy"))
            }));
            var plugin = new Plugin();
            SetField(plugin, "ClipboardMonitor", null);
            SetField(plugin, "Context", new PluginInitContext(null, Ioc.Default.GetRequiredService<IPublicAPI>()));
            SetField(plugin, "Settings", new Settings());
            SetField(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
            SetField(plugin, "ScoreHelper", new ScoreHelper(10000));
            SetField(plugin, "Database", database);
            SetField(plugin, "RecordsList", list);
            List<Result>? published = null;
            plugin.ResultsUpdated += (_, args) => { if (args.Query.Search == "synthetic") published = args.Results; };
            var initialResults = plugin.QueryAsync(QueryFor("synthetic"), CancellationToken.None).GetAwaiter().GetResult();
            if (initialResults.Count(result => result.ContextData is ClipboardDataPair) != records.Length)
                throw new InvalidOperationException("Synthetic initial results missing");
            var cleanup = typeof(Plugin).GetMethod("RunCleanupWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Cleanup test seam missing");
            var report = ((Task<CleanupReport>)cleanup.Invoke(plugin, new object[] { CleanupWork.ManualThorough })!).GetAwaiter().GetResult();
            if (report.RecordsRemoved != 2 || report.Errors != 0 || list.Count != 3 ||
                list.Any(item => item.ClipboardData.HashId == "saved") || !list.Any(item => item.ClipboardData.HashId == "temporary") ||
                !list.Any(item => item.ClipboardData.HashId == "pinned"))
                throw new InvalidOperationException("Synthetic plugin cleanup failed");
            if (published is null || published.Any(result => result.ContextData is ClipboardDataPair pair && pair.ClipboardData.HashId == "saved") ||
                published.Count(result => result.ContextData is ClipboardDataPair) != list.Count)
                throw new InvalidOperationException("Synthetic displayed results were not refreshed");
            if (database.GetAllRecordsAsync(false).GetAwaiter().GetResult().Any(record => record.HashId == "unloaded-saved"))
                throw new InvalidOperationException("Synthetic unloaded saved duplicate survived cleanup");
            var settings = new SettingsViewModel(plugin);
            settings.ClearKeyword = "cleanup";
            settings.CleanupKeyword = "clear";
            if (settings.ClearKeyword != "clear" || settings.CleanupKeyword != "cleanup" || string.IsNullOrEmpty(settings.CleanupFeedback))
                throw new InvalidOperationException("Synthetic cleanup keyword conflict validation failed");
            var exact = plugin.QueryAsync(QueryFor("cleanup"), CancellationToken.None).GetAwaiter().GetResult();
            var longer = plugin.QueryAsync(QueryFor("cleanup extra"), CancellationToken.None).GetAwaiter().GetResult();
            if (exact.Count != 1 || exact[0].Score != int.MaxValue || longer.Count != 0 || list.Count != 3)
                throw new InvalidOperationException("Synthetic cleanup query validation failed");
            TestUnloadedSavedKeeper();
            TestManualActionRefresh();
            TestCapacityProtection(plugin);
            TestDisabledEvidenceRefresh(plugin);
            TestManualImageEvidenceRetention();
            TestOptOutImageCache(plugin);
            TestSyntheticCacheScan(plugin, database);
            Console.WriteLine("dedup plugin synthetic record and cache integration checks passed");
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void TestManualActionRefresh()
    {
        var database = new SqliteDatabase("synthetic-manual-refresh", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            ClipboardData Item(string id, int minute) => new("cleanup synthetic", DataType.PlainText, false)
            {
                HashId = id, SenderApp = "synthetic", InitScore = 0,
                CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = false, Saved = false
            };
            var list = new LinkedList<ClipboardDataPair>(new[] { Item("old-manual", 1), Item("new-manual", 2) }.Select(data => new ClipboardDataPair
            {
                ClipboardData = data,
                PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must stay lazy"))
            }));
            var plugin = new Plugin();
            SetField(plugin, "ClipboardMonitor", null);
            SetField(plugin, "Context", new PluginInitContext(null, Ioc.Default.GetRequiredService<IPublicAPI>()));
            SetField(plugin, "Settings", new Settings());
            SetField(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
            SetField(plugin, "ScoreHelper", new ScoreHelper(10000));
            SetField(plugin, "Database", database);
            SetField(plugin, "RecordsList", list);
            var results = plugin.QueryAsync(QueryFor("cleanup"), CancellationToken.None).GetAwaiter().GetResult();
            var action = results.Single(result => result.Score == int.MaxValue);
            if (results.Count(result => result.ContextData is ClipboardDataPair) != 2)
                throw new InvalidOperationException("Synthetic cleanup query is missing records");
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            plugin.ResultsUpdated += (_, args) =>
            {
                if (args.Results.Contains(action) && args.Results.Count(result => result.ContextData is ClipboardDataPair) == 1 &&
                    args.Results.All(result => result.ContextData is not ClipboardDataPair pair || pair.ClipboardData.HashId != "old-manual"))
                    refreshed.TrySetResult();
            };
            action.Action!(null!);
            refreshed.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var coordinator = (CleanupRunCoordinator)typeof(Plugin).GetField("cleanupCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            coordinator.StopAsync().GetAwaiter().GetResult();
        }
        finally { database.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void TestUnloadedSavedKeeper()
    {
        var database = new SqliteDatabase("synthetic-unloaded-keeper", new ClipboardPlus(), SqliteCacheMode.Private, SqliteOpenMode.Memory);
        try
        {
            database.InitializeDatabaseAsync().GetAwaiter().GetResult();
            var keeper = Record("unloaded-keeper", 4, true);
            var duplicate = Record("loaded-duplicate", 3, true);
            database.AddRecordsAsync([keeper, duplicate], true).GetAwaiter().GetResult();
            var list = new LinkedList<ClipboardDataPair>([new ClipboardDataPair
            {
                ClipboardData = duplicate,
                PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must remain lazy"))
            }]);
            var plugin = new Plugin();
            SetField(plugin, "ClipboardMonitor", null);
            SetField(plugin, "Context", new PluginInitContext(null, Ioc.Default.GetRequiredService<IPublicAPI>()));
            SetField(plugin, "Settings", new Settings());
            SetField(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
            SetField(plugin, "ScoreHelper", new ScoreHelper(10000));
            SetField(plugin, "Database", database);
            SetField(plugin, "RecordsList", list);
            var cleanup = typeof(Plugin).GetMethod("RunCleanupWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var report = ((Task<CleanupReport>)cleanup.Invoke(plugin, [CleanupWork.ManualThorough])!).GetAwaiter().GetResult();
            var survivors = database.GetAllRecordsAsync(false).GetAwaiter().GetResult();
            if (report.RecordsRemoved != 1 || list.Count != 1 || list.First!.Value.ClipboardData.HashId != keeper.HashId ||
                survivors.Count != 1 || survivors[0].HashId != keeper.HashId)
                throw new InvalidOperationException("Synthetic unloaded saved keeper was not preserved");
        }
        finally { database.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void TestCapacityProtection(Plugin plugin)
    {
        SetField(plugin, "Settings", new Settings { AutomaticCleanup = true, MaxRecords = 2 });
        var list = new LinkedList<ClipboardDataPair>();
        SetField(plugin, "RecordsList", list);
        var add = typeof(Plugin).GetMethod("AddClipboardDataItem", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(ClipboardData)], null)!;
        var unique = Record("unique", 1, false);
        unique = new ClipboardData("unique synthetic", DataType.PlainText, false)
        {
            HashId = unique.HashId, SenderApp = unique.SenderApp, InitScore = unique.InitScore,
            CreateTime = unique.CreateTime, Pinned = false, Saved = false
        };
        add.Invoke(plugin, [unique]);
        add.Invoke(plugin, [Record("older", 2, false)]);
        add.Invoke(plugin, [Record("newer", 3, false)]);
        if (list.Count != 3 || !list.Any(pair => pair.ClipboardData.HashId == "unique"))
            throw new InvalidOperationException("Synthetic unique record was evicted before verification");
        var cleanup = typeof(Plugin).GetMethod("RunCleanupWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var report = ((Task<CleanupReport>)cleanup.Invoke(plugin, [CleanupWork.AutomaticBestEffort])!).GetAwaiter().GetResult();
        if (report.RecordsRemoved != 1 || list.Count != 2 || !list.Any(pair => pair.ClipboardData.HashId == "unique"))
            throw new InvalidOperationException("Synthetic duplicate cleanup did not protect capacity");

        list.Clear();
        add.Invoke(plugin, [unique]);
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 11, 12, 13, 255 }, 4);
        image.Freeze();
        ClipboardData Image(string id, int minute) => new(image, DataType.Image, false)
        {
            HashId = id, SenderApp = "synthetic", InitScore = 0, CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute),
            Pinned = false, Saved = false
        };
        add.Invoke(plugin, [Image("old-image", 2)]);
        add.Invoke(plugin, [Image("new-image", 3)]);
        report = ((Task<CleanupReport>)cleanup.Invoke(plugin, [CleanupWork.AutomaticBestEffort])!).GetAwaiter().GetResult();
        if (report.Skipped == 0 || list.Count != 3 || !list.Any(pair => pair.ClipboardData.HashId == "unique"))
            throw new InvalidOperationException("Unverified images did not preserve synthetic overflow");

        list.Clear();
        for (var i = 0; i < 3; i++)
        {
            var item = new ClipboardData($"distinct synthetic {i}", DataType.PlainText, false)
            {
                HashId = $"distinct-{i}", SenderApp = "synthetic", InitScore = 0,
                CreateTime = new DateTime(2026, 1, 1).AddMinutes(i), Pinned = false, Saved = false
            };
            add.Invoke(plugin, [item]);
        }
        ((Task<CleanupReport>)cleanup.Invoke(plugin, [CleanupWork.AutomaticBestEffort])!).GetAwaiter().GetResult();
        if (list.Count != 2 || list.Any(pair => pair.ClipboardData.HashId == "distinct-0"))
            throw new InvalidOperationException("Verified unique overflow was not trimmed");
    }

    private static void TestDisabledEvidenceRefresh(Plugin plugin)
    {
        SetField(plugin, "Settings", new Settings { AutomaticCleanup = false });
        var coordinator = typeof(Plugin).GetField("cleanupCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        var refresh = typeof(Plugin).GetMethod("RequestWindowsEvidenceRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 0; i < 100; i++) refresh.Invoke(plugin, null);
        if ((bool)typeof(CleanupRunCoordinator).GetField("workerRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!)
            throw new InvalidOperationException("Disabled cleanup scheduled unneeded evidence work");
    }

    private static void TestManualImageEvidenceRetention()
    {
        var plugin = new Plugin();
        var settings = new Settings { AutomaticCleanup = true };
        SetField(plugin, "Settings", settings);
        SetField(plugin, "Context", new PluginInitContext(null, Ioc.Default.GetRequiredService<IPublicAPI>()));
        var helper = typeof(Plugin).GetField("WindowsClipboardHelper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        typeof(WindowsClipboardHelper).GetField("pinStateTrusted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(helper, true);
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 21, 22, 23, 255 }, 4);
        image.Freeze();
        ClipboardData Image(string id, int minute) => new(image, DataType.Image, false)
        {
            HashId = id, SenderApp = "synthetic", InitScore = 0, CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute),
            Pinned = false, Saved = false
        };
        object boxed = Image("synthetic-windows-image", 1);
        typeof(ClipboardData).GetProperty("WindowsHistoryOrigin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(boxed, true);
        var windows = new ClipboardDataPair { ClipboardData = (ClipboardData)boxed,
            PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must stay lazy")) };
        var keeper = new ClipboardDataPair { ClipboardData = Image("synthetic-image-keeper", 2),
            PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must stay lazy")) };
        SetField(plugin, "RecordsList", new LinkedList<ClipboardDataPair>([keeper, windows]));
        var pinEvent = typeof(Plugin).GetMethod("WindowsClipboardHelper_OnHistoryItemPinUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        pinEvent.Invoke(plugin, [null, ("synthetic-windows-image", true)]);
        pinEvent.Invoke(plugin, [null, ("synthetic-windows-image", false)]);
        if (windows.ClipboardData.Pinned || !windows.ClipboardData.FromWindowsClipboardHistory() ||
            DuplicateRecordPlanner.Plan([keeper.ClipboardData, windows.ClipboardData], true).Count != 0)
            throw new InvalidOperationException("Synthetic pin events lost Windows origin");
        var generation = (long)typeof(Plugin).GetField("windowsEvidenceGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        var refresh = typeof(Plugin).GetMethod("RefreshWindowsEvidenceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hidden = typeof(Plugin).GetMethod("IsWindowsSuppressed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Task<CleanupReport>)refresh.Invoke(plugin, [CleanupWork.ManualThorough, true, generation])!).GetAwaiter().GetResult();
        if (!(bool)hidden.Invoke(plugin, [windows])!) throw new InvalidOperationException("Manual synthetic image was not suppressed");
        ((Task<CleanupReport>)refresh.Invoke(plugin, [CleanupWork.AutomaticBestEffort, false, generation])!).GetAwaiter().GetResult();
        if (!(bool)hidden.Invoke(plugin, [windows])!) throw new InvalidOperationException("Best effort discarded valid manual image evidence");
        windows.TogglePinned();
        if ((bool)hidden.Invoke(plugin, [windows])!) throw new InvalidOperationException("Pinned synthetic Windows item was hidden");
    }

    private static void TestOptOutImageCache(Plugin plugin)
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-optout-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cachePath = typeof(PathHelper).GetProperty("ImageCachePath")!;
        var previous = cachePath.GetValue(null);
        try
        {
            cachePath.SetValue(null, root);
            var settings = new Settings { AutomaticCleanup = false, CacheImages = true };
            SetField(plugin, "Settings", settings);
            SetField(plugin, "RecordsList", new LinkedList<ClipboardDataPair>());
            SetField(plugin, "Context", new PluginInitContext(null, Ioc.Default.GetRequiredService<IPublicAPI>()));
            var pixels = new byte[1024 * 1024 * 4];
            new Random(5678).NextBytes(pixels);
            var image = BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Bgra32, null, pixels, 4096);
            image.Freeze();
            var capture = typeof(Plugin).GetMethod("GetClipboardDataItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var savesBefore = Volatile.Read(ref SyntheticHost.SettingsSaveCalls);
            ClipboardData Create(string id) => (ClipboardData)capture.Invoke(plugin,
                [image, DataType.Image, id, new DateTime(2026, 1, 1), SourceApplication.NULL, "", "", true, null])!;
            var timer = Stopwatch.StartNew();
            var first = Create("synthetic-image-one");
            var firstMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var second = Create("synthetic-image-two");
            var secondMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var safeFile = FileUtils.SaveNewImageCache(first, root, "synthetic-safe-benchmark", out var measuredOwnership);
            var safeFileMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var legacy = FileUtils.SaveImageCache(first, root, "synthetic-legacy-benchmark");
            var legacyMs = timer.Elapsed.TotalMilliseconds;
            if (string.IsNullOrEmpty(legacy) || string.IsNullOrEmpty(safeFile) || measuredOwnership is null)
                throw new InvalidOperationException("Synthetic cache benchmark failed");
            Console.WriteLine($"dedup optout full image capture first/repeat {firstMs:F2}/{secondMs:F2} ms; safe/legacy file write {safeFileMs:F2}/{legacyMs:F2} ms");
            if (string.IsNullOrEmpty(first.CachedImagePath) || first.CachedImagePath == second.CachedImagePath ||
                !File.Exists(first.CachedImagePath) || !File.Exists(second.CachedImagePath) ||
                settings.OwnedCacheFiles.Count != 2 || settings.OwnedCacheFiles.Any(item => item.WindowsAssociated))
                throw new InvalidOperationException("Opt-out synthetic capture did not preserve owned files");
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref SyntheticHost.SettingsSaveCalls) >= savesBefore + 2, TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("Synthetic provenance saves did not complete");
        }
        finally
        {
            cachePath.SetValue(null, previous);
            Directory.Delete(root, true);
        }
    }

    private static void TestSyntheticCacheScan(Plugin plugin, SqliteDatabase database)
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-synthetic-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cachePath = typeof(PathHelper).GetProperty("ImageCachePath")!;
        var previous = cachePath.GetValue(null);
        try
        {
            cachePath.SetValue(null, root);
            // No settings storage is invoked in this synthetic-only integration test.
            SetField(plugin, "Context", null);
            var settings = new Settings();
            SetField(plugin, "Settings", settings);
            var keep = Path.Combine(root, "keeper.png");
            var owned = Path.Combine(root, "owned.png");
            var unknown = Path.Combine(root, "unknown.png");
            File.WriteAllBytes(keep, new byte[] { 4, 5, 6 });
            using (var stream = new FileStream(owned, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(new byte[] { 4, 5, 6 }); stream.Flush();
                settings.OwnedCacheFiles.Add(ManagedCacheFile.IdentifyNewFile(root, owned, stream)
                    ?? throw new InvalidOperationException("Synthetic ownership unavailable"));
            }
            File.WriteAllBytes(unknown, new byte[] { 4, 5, 6 });
            var record = Record("cache-keeper", 6, true);
            record.CachedImagePath = keep;
            database.AddOneRecordAsync(record, true).GetAwaiter().GetResult();
            var scan = typeof(Plugin).GetMethod("ScanOrphanCacheDuplicatesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var report = ((Task<CleanupReport>)scan.Invoke(plugin, [null])!).GetAwaiter().GetResult();
            if (report.CacheFilesRemoved != 1 || report.Errors != 0 || File.Exists(owned) ||
                !File.Exists(keep) || !File.Exists(unknown))
                throw new InvalidOperationException("Synthetic cache scan safety failed");

            var windowsFile = Path.Combine(root, "windows-associated.png");
            using (var stream = new FileStream(windowsFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(new byte[] { 4, 5, 6 }); stream.Flush();
                var identity = ManagedCacheFile.IdentifyNewFile(root, windowsFile, stream)
                    ?? throw new InvalidOperationException("Synthetic Windows cache ownership unavailable");
                settings.OwnedCacheFiles.Add(identity with { WindowsAssociated = true });
            }
            report = ((Task<CleanupReport>)scan.Invoke(plugin, [null])!).GetAwaiter().GetResult();
            if (!File.Exists(windowsFile) || report.CacheFilesRemoved != 0)
                throw new InvalidOperationException("Manual scan must preserve Windows-associated cache");

            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
            image.Freeze();
            var surviving = new ClipboardData(image, DataType.Image, false)
            {
                HashId = "synthetic-survivor", SenderApp = "synthetic", InitScore = 1,
                CreateTime = new DateTime(2026, 1, 1), CachedImagePath = keep, Saved = false, Pinned = false
            };
            var list = new LinkedList<ClipboardDataPair>();
            list.AddFirst(new ClipboardDataPair { ClipboardData = surviving,
                PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must stay lazy")) });
            SetField(plugin, "RecordsList", list);
            var queue = (ConcurrentQueue<string>)typeof(Plugin).GetField("removedWindowsCachePaths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
            var process = typeof(Plugin).GetMethod("CleanupRemovedWindowsCacheAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            queue.Enqueue(windowsFile);
            ((Task<CleanupReport>)process.Invoke(plugin, [true])!).GetAwaiter().GetResult();
            if (!File.Exists(windowsFile)) throw new InvalidOperationException("Disabled automatic cleanup deleted Windows cache");
            settings.AutomaticCleanup = true;
            queue.Enqueue(windowsFile);
            report = ((Task<CleanupReport>)process.Invoke(plugin, [false])!).GetAwaiter().GetResult();
            if (!File.Exists(windowsFile) || report.CacheFilesRemoved != 0)
                throw new InvalidOperationException("Best-effort cleanup verified an image cache file");
            queue.Enqueue(windowsFile);
            report = ((Task<CleanupReport>)process.Invoke(plugin, [true])!).GetAwaiter().GetResult();
            if (report.CacheFilesRemoved != 1 || File.Exists(windowsFile) || !File.Exists(keep))
                throw new InvalidOperationException("Synthetic Windows-removal cache cleanup failed");
        }
        finally
        {
            cachePath.SetValue(null, previous);
            Directory.Delete(root, true);
        }
    }

    private static Query QueryFor(string search)
    {
        var query = new Query { ActionKeyword = "cbp", SearchTerms = search.Split(' ') };
        typeof(Query).GetProperty("Search")!.SetValue(query, search);
        return query;
    }

    private static ClipboardData Record(string id, int minute, bool saved, bool pinned = false) => new("synthetic", DataType.PlainText, false)
    {
        HashId = id, SenderApp = "synthetic", InitScore = minute,
        CreateTime = new DateTime(2026, 1, 1).AddMinutes(minute), Pinned = pinned, Saved = saved,
        EncryptKeyMd5 = StringUtils.EncryptKeyMd5
    };

    private static void SetField(Plugin plugin, string name, object? value) =>
        (typeof(Plugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Plugin test seam changed")).SetValue(plugin, value);
}
