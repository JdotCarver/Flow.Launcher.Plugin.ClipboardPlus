// DuplicatePluginIntegration.cs
// Exercises the plugin cleanup against synthetic records and in-memory SQLite.
using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
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
            Console.WriteLine("dedup plugin synthetic record integration checks passed");
        }
        finally
        {
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
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
