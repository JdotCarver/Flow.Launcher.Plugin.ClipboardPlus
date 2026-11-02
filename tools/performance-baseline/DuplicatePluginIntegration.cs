// DuplicatePluginIntegration.cs
// Exercises the plugin cleanup against synthetic records and in-memory SQLite.
using System.Globalization;
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
            database.AddRecordsAsync(records.Where(record => record.Saved), true).GetAwaiter().GetResult();
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
            var cleanup = typeof(Plugin).GetMethod("RunCleanupWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Cleanup test seam missing");
            var report = ((Task<CleanupReport>)cleanup.Invoke(plugin, new object[] { CleanupWork.ManualThorough })!).GetAwaiter().GetResult();
            if (report.RecordsRemoved != 2 || report.Errors != 0 || list.Count != 2 ||
                list.Any(item => item.ClipboardData.HashId is "saved" or "temporary") ||
                !list.Any(item => item.ClipboardData.HashId == "pinned"))
                throw new InvalidOperationException("Synthetic plugin cleanup failed");
            var settings = new SettingsViewModel(plugin);
            settings.ClearKeyword = "cleanup";
            settings.CleanupKeyword = "clear";
            if (settings.ClearKeyword != "clear" || settings.CleanupKeyword != "cleanup" || string.IsNullOrEmpty(settings.CleanupFeedback))
                throw new InvalidOperationException("Synthetic cleanup keyword conflict validation failed");
            var exact = plugin.QueryAsync(QueryFor("cleanup"), CancellationToken.None).GetAwaiter().GetResult();
            var longer = plugin.QueryAsync(QueryFor("cleanup extra"), CancellationToken.None).GetAwaiter().GetResult();
            if (exact.Count != 1 || exact[0].Score != int.MaxValue || longer.Count != 0 || list.Count != 2)
                throw new InvalidOperationException("Synthetic cleanup query validation failed");
            TestSyntheticCacheScan(plugin, database);
            Console.WriteLine("dedup plugin synthetic record and cache integration checks passed");
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
            var report = ((Task<CleanupReport>)scan.Invoke(plugin, null)!).GetAwaiter().GetResult();
            if (report.CacheFilesRemoved != 1 || report.Errors != 0 || File.Exists(owned) ||
                !File.Exists(keep) || !File.Exists(unknown))
                throw new InvalidOperationException("Synthetic cache scan safety failed");
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
