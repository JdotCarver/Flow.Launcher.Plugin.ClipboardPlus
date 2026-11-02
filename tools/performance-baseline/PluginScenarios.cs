// PluginScenarios.cs
// This host never calls InitAsync, starts a monitor, reads settings, or accesses the system clipboard.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using Flow.Launcher.Plugin;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;
using Flow.Launcher.Plugin.SharedModels;
using Microsoft.Extensions.DependencyInjection;
using Plugin = Flow.Launcher.Plugin.ClipboardPlus.ClipboardPlus;
using Settings = Flow.Launcher.Plugin.ClipboardPlus.Settings;

internal static class PluginScenarios
{
    public static void Run()
    {
        var hostApi = DispatchProxy.Create<IPublicAPI, SyntheticHost>();
        Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton(hostApi).BuildServiceProvider());
        var plugin = new Plugin(); // The constructor creates a monitor, but does not start it.
        SetField(plugin, "ClipboardMonitor", null);
        SetField(plugin, "Settings", new Settings { MaxRecords = 20000 });
        SetField(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
        SetField(plugin, "ScoreHelper", new ScoreHelper(10000));

        foreach (int size in new[] { 100, 333, 334, 1000, 10000 })
        {
            var list = new LinkedList<ClipboardDataPair>();
            for (int i = 0; i < size; i++)
            {
                var data = SyntheticRecord(i);
                list.AddLast(new ClipboardDataPair
                {
                    ClipboardData = data,
                    PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must remain lazy"))
                });
            }
            SetField(plugin, "RecordsList", list);
            foreach (var search in new[] { "", "synthetic", "unmatched" })
            {
                var query = CreateQuery(search);
                int expected = search switch { "" => size + 2, "synthetic" => size, _ => 0 };
                // Empty queries also include three actions, but the disconnected monitor action is absent.
                Measure($"plugin query {size}, {(search == "" ? "empty" : search == "synthetic" ? "all-match" : "no-match")}", () =>
                {
                    var results = plugin.QueryAsync(query, CancellationToken.None).GetAwaiter().GetResult();
                    if (results.Count != expected) throw new InvalidOperationException("Unexpected synthetic result count");
                });
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            if (plugin.QueryAsync(CreateQuery("synthetic"), cancelled.Token).GetAwaiter().GetResult().Count != 0)
                throw new InvalidOperationException("Cancellation did not return an empty list");
        }

        SetField(plugin, "RecordsList", new LinkedList<ClipboardDataPair>());
        var add = typeof(Plugin).GetMethod("AddClipboardDataItem", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(ClipboardData)], null)!;
        int sequence = 0;
        Measure("plugin synthetic text add (includes forced GC)", () => add.Invoke(plugin, [SyntheticRecord(sequence++)]));
    }

    private static ClipboardData SyntheticRecord(int index) => new($"synthetic clipboard entry {index}", DataType.PlainText, false)
    {
        HashId = index.ToString(CultureInfo.InvariantCulture),
        SenderApp = "synthetic.exe",
        InitScore = index,
        CreateTime = new DateTime(2025, 1, 1),
        EncryptKeyMd5 = string.Empty,
        CachedImagePath = string.Empty,
        Pinned = false,
        Saved = false
    };

    private static Query CreateQuery(string text)
    {
        var query = new Query { ActionKeyword = "cbp", SearchTerms = text.Length == 0 ? [] : [text] };
        typeof(Query).GetProperty("Search")!.SetValue(query, text);
        return query;
    }

    private static void SetField(Plugin plugin, string name, object? value) =>
        (typeof(Plugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Plugin test seam changed")).SetValue(plugin, value);

    private static void Measure(string name, Action action)
    {
        action();
        var times = new double[5];
        var allocations = new long[5];
        for (int i = 0; i < times.Length; i++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            action();
            timer.Stop();
            times[i] = timer.Elapsed.TotalMilliseconds;
            allocations[i] = GC.GetTotalAllocatedBytes(true) - before;
        }
        Array.Sort(times);
        Array.Sort(allocations);
        Console.WriteLine($"{name}: ms min/median/max={times[0]:F2}/{times[2]:F2}/{times[4]:F2}; median allocated={allocations[2] / 1048576.0:F2} MiB");
    }
}

// An intentionally simple deterministic matcher: this is not Flow Launcher's fuzzy matcher.
// Unexpected host interactions fail closed rather than touching the real host.
public class SyntheticHost : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method == null) throw new InvalidOperationException("Host method missing");
        if (method.Name.StartsWith("Log", StringComparison.Ordinal)) return null;
        if (method.Name == "GetTranslation") return "synthetic";
        if (method.Name == "FuzzySearch")
        {
            bool found = ((string)args![1]!).Contains((string)args[0]!, StringComparison.OrdinalIgnoreCase);
            return new MatchResult(found, SearchPrecisionScore.Regular) { RawScore = found ? 100 : 0 };
        }
        throw new InvalidOperationException("Unexpected host operation: " + method.Name);
    }
}
