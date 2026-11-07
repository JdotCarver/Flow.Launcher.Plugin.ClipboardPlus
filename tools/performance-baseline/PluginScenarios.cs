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
        SyntheticHost.Matcher = new Flow.Launcher.Infrastructure.StringMatcher(null!)
        {
            UserSettingSearchPrecision = SearchPrecisionScore.Regular
        };
        // Independently verify a few known upstream matcher outcomes before measuring.
        if (SyntheticHost.Matcher.FuzzyMatch("inst", "Install Package").RawScore <= 0 ||
            SyntheticHost.Matcher.FuzzyMatch("zzzz", "Install Package").RawScore != 0)
            throw new InvalidOperationException("Upstream matcher sanity check failed");
        var hostApi = DispatchProxy.Create<IPublicAPI, SyntheticHost>();
        Ioc.Default.ConfigureServices(new ServiceCollection().AddSingleton(hostApi).BuildServiceProvider());
        var plugin = new Plugin(); // The constructor creates a monitor, but does not start it.
        SetField(plugin, "ClipboardMonitor", null);
        SetField(plugin, "Settings", new Settings { MaxRecords = 20000 });
        SetField(plugin, "CultureInfo", CultureInfo.GetCultureInfo("en-US"));
        SetField(plugin, "ScoreHelper", new ScoreHelper(10000));

        TestActionBookends(plugin);
        ProbeSyntheticEmailAtSearch(plugin);

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

        QueryInvestigation.Run(plugin);
        MeasureQueryRetention(plugin);
        MeasureContendedCapture(plugin);
        SetField(plugin, "RecordsList", new LinkedList<ClipboardDataPair>());
        var add = typeof(Plugin).GetMethod("AddClipboardDataItem", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(ClipboardData)], null)!;
        int sequence = 0;
        Measure("plugin synthetic text add (includes forced GC)", () =>
        {
            int index = sequence++;
            var record = plugin.GetClipboardDataItem($"synthetic clipboard entry {index}", DataType.PlainText,
                index.ToString(CultureInfo.InvariantCulture), new DateTime(2025, 1, 1), SourceApplication.NULL, "", "");
            if (record.IsNull()) throw new InvalidOperationException("Synthetic capture rejected");
            add.Invoke(plugin, [record]);
        });
    }

    private static void ProbeSyntheticEmailAtSearch(Plugin plugin)
    {
        var values = new[] { "a@invalid.test", "longlocalpart@invalid.test",
            "averyveryveryverylonglocalpart@invalid.test", "synthetic without symbol" };
        var pairs = values.Select((text, index) => new ClipboardDataPair
        {
            ClipboardData = new ClipboardData(text, DataType.PlainText, false)
            {
                HashId = index.ToString(CultureInfo.InvariantCulture), SenderApp = "synthetic", InitScore = index,
                CreateTime = new DateTime(2025, 1, 1), Pinned = false, Saved = false
            },
            PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must remain lazy") )
        }).ToArray();
        SetField(plugin, "RecordsList", new LinkedList<ClipboardDataPair>(pairs));
        var counts = new List<int>();
        foreach (var precision in new[] { SearchPrecisionScore.Regular, SearchPrecisionScore.Low })
        {
            SyntheticHost.Matcher.UserSettingSearchPrecision = precision;
            var expected = values.Count(value => SyntheticHost.Matcher.FuzzyMatch("@", value).IsSearchPrecisionScoreMet());
            var results = plugin.QueryAsync(CreateQuery("@"), CancellationToken.None).GetAwaiter().GetResult();
            if (results.Count != expected || results.Any(result => !((ClipboardDataPair)result.ContextData).ClipboardData.GetText(CultureInfo.InvariantCulture).Contains('@')))
                throw new InvalidOperationException("Synthetic plugin query and matcher differed");
            counts.Add(results.Count);
        }
        if (counts[0] != 1 || counts[1] != 2) throw new InvalidOperationException("Unexpected synthetic email precision results");
        Console.WriteLine($"SyntheticAtSearch regular={counts[0]} low={counts[1]} total=3");
        SyntheticHost.Matcher.UserSettingSearchPrecision = SearchPrecisionScore.Regular;
    }

    private static void TestActionBookends(Plugin plugin)
    {
        // Includes the first imported score, negative scores, ties and a pin.
        var data = new[]
        {
            SyntheticRecord(0) with { InitScore = 1 },
            SyntheticRecord(1) with { InitScore = -10000 },
            SyntheticRecord(2) with { InitScore = 10001 },
            SyntheticRecord(3) with { InitScore = 10001 },
            SyntheticRecord(4) with { InitScore = 20001, Pinned = true }
        };
        SetField(plugin, "RecordsList", new LinkedList<ClipboardDataPair>(data.Select(record => new ClipboardDataPair
        {
            ClipboardData = record,
            PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must remain lazy"))
        })));
        var refresh = typeof(Plugin).GetMethod("PublishCurrentResultsAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Refresh test seam missing");
        foreach (var order in Enum.GetValues<RecordOrder>())
        foreach (var actionTop in new[] { false, true })
        {
            var settings = new Settings { ActionTop = actionTop, RecordOrder = order };
            SetField(plugin, "Settings", settings);
            List<Result>? refreshed = null;
            void Capture(object? sender, ResultUpdatedEventArgs args) => refreshed = args.Results;
            plugin.ResultsUpdated += Capture;
            try
            {
                var results = plugin.QueryAsync(CreateQuery(""), CancellationToken.None).GetAwaiter().GetResult();
                AssertBookends(results, order, actionTop);
                ((Task)refresh.Invoke(plugin, null)!).GetAwaiter().GetResult();
                if (refreshed is null) throw new InvalidOperationException("Synthetic live refresh missing");
                AssertBookends(refreshed, order, actionTop);
            }
            finally { plugin.ResultsUpdated -= Capture; }
        }
        SetField(plugin, "Settings", new Settings { MaxRecords = 20000 });
    }

    private static void AssertBookends(List<Result> results, RecordOrder order, bool actionTop)
    {
        var actions = results.Where(result => result.ContextData is not ClipboardDataPair).ToArray();
        var records = results.Where(result => result.ContextData is ClipboardDataPair).ToArray();
        if (actions.Length != 2 || records.Length != 5 ||
            actions.Any(action => records.Any(record => actionTop ? action.Score <= record.Score : action.Score >= record.Score)))
            throw new InvalidOperationException("Actions did not bookend synthetic records");
        foreach (var left in records)
        foreach (var right in records)
        {
            var leftData = ((ClipboardDataPair)left.ContextData).ClipboardData;
            var rightData = ((ClipboardDataPair)right.ContextData).ClipboardData;
            if (Math.Sign(left.Score.CompareTo(right.Score)) != Math.Sign(leftData.GetScore(order).CompareTo(rightData.GetScore(order))))
                throw new InvalidOperationException("Record ordering or ties changed");
        }
    }

    private static void MeasureQueryRetention(Plugin plugin)
    {
        var query = CreateQuery("synthetic");
        RunQueries(plugin, query, 3);
        long before = GC.GetTotalMemory(true);
        RunQueries(plugin, query, 20);
        long after = GC.GetTotalMemory(true);
        Console.WriteLine($"plugin repeated query managed heap delta: {(after - before) / 1048576.0:F2} MiB (20 queries, full GC)");
    }

    private static void RunQueries(Plugin plugin, Query query, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (plugin.QueryAsync(query, CancellationToken.None).GetAwaiter().GetResult().Count != 10000)
                throw new InvalidOperationException("Unexpected synthetic query count");
        }
    }

    private static void MeasureContendedCapture(Plugin plugin)
    {
        var recordLock = (SemaphoreSlim)(typeof(Plugin).GetField("RecordsLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(plugin) ?? throw new InvalidOperationException("Record lock missing"));
        var delays = new double[5];
        for (int i = 0; i < delays.Length; i++)
        {
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            SyntheticHost.BeforeFirstMatch = () => { entered.Set(); resume.Wait(); };
            var query = plugin.QueryAsync(CreateQuery("synthetic"), CancellationToken.None);
            if (!entered.Wait(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Query did not enter matcher");
            var waitTimer = Stopwatch.StartNew();
            var wait = recordLock.WaitAsync();
            resume.Set();
            wait.GetAwaiter().GetResult();
            waitTimer.Stop();
            recordLock.Release();
            if (query.GetAwaiter().GetResult().Count != 10000) throw new InvalidOperationException("Contended query count mismatch");
            delays[i] = waitTimer.Elapsed.TotalMilliseconds;
        }
        Array.Sort(delays);
        Console.WriteLine($"plugin capture lock wait during query: ms min/median/max={delays[0]:F2}/{delays[2]:F2}/{delays[4]:F2}");
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

// Uses the vendored Flow Launcher 2.1.4 matcher with no alphabet translation.
// Unexpected host interactions fail closed rather than touching the real host.
public class SyntheticHost : DispatchProxy
{
    public static Action? BeforeFirstMatch;
    public static int SettingsSaveCalls;
    public static int ConfirmationCalls;
    public static Flow.Launcher.Infrastructure.StringMatcher Matcher = null!;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method == null) throw new InvalidOperationException("Host method missing");
        if (method.Name.StartsWith("Log", StringComparison.Ordinal) || method.Name == "ReQuery") return null;
        if (method.Name == "SaveSettingJsonStorage") { Interlocked.Increment(ref SettingsSaveCalls); return null; }
        if (method.Name == "GetTranslation") return "synthetic";
        if (method.Name == "ShowMsgBox") { Interlocked.Increment(ref ConfirmationCalls); return System.Windows.MessageBoxResult.No; }
        if (method.Name == "FuzzySearch")
        {
            Interlocked.Exchange(ref BeforeFirstMatch, null)?.Invoke();
            return Matcher.FuzzyMatch((string)args![0]!, (string)args[1]!);
        }
        throw new InvalidOperationException("Unexpected host operation: " + method.Name);
    }
}
