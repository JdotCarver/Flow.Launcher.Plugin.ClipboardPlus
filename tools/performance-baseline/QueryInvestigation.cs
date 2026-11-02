// QueryInvestigation.cs
// Synthetic component experiments; never reads a clipboard or personal history.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using Flow.Launcher.Plugin;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Plugin = Flow.Launcher.Plugin.ClipboardPlus.ClipboardPlus;

internal static class QueryInvestigation
{
    public static void Run(Plugin plugin)
    {
        var factory = (Func<ClipboardDataPair, string?, Result?>)typeof(Plugin)
            .GetMethod("GetResultFromClipboardData", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<ClipboardDataPair, string?, Result?>), plugin);
        foreach (var (count, longText) in new[] { (50, false), (100, false), (200, false), (333, false), (1000, false), (10000, false), (1000, true) })
        {
            var records = CreateRecords(count, longText);
            foreach (var (label, search, expected) in new[]
            {
                ("all", "synthetic", count),
                ("half", "target", (count + 1) / 2),
                ("none", "zzzz", 0)
            })
            {
                var sequential = SearchSequential(records, search, factory);
                var parallel = SearchParallel(records, search, factory);
                VerifyEquivalent(sequential, parallel, expected);
                string name = $"query experiment {count} {(longText ? "long" : "short")} {label}";
                Measure($"{name} matcher only", () =>
                {
                    int matched = 0;
                    foreach (var pair in records)
                    {
                        if (SyntheticHost.Matcher.FuzzyMatch(search, (string)pair.ClipboardData.Data!).IsSearchPrecisionScoreMet()) matched++;
                    }
                    if (matched != expected) throw new InvalidOperationException("Synthetic matcher count mismatch");
                });
                MeasurePair($"{name}", () =>
                {
                    if (SearchSequential(records, search, factory).Count != expected)
                        throw new InvalidOperationException("Synthetic serial count mismatch");
                }, () =>
                {
                    if (SearchParallel(records, search, factory).Count != expected)
                        throw new InvalidOperationException("Synthetic parallel count mismatch");
                });
            }
        }
        Console.WriteLine("query experiment result equivalence checks passed");
    }

    private static LinkedList<ClipboardDataPair> CreateRecords(int count, bool longText)
    {
        var records = new LinkedList<ClipboardDataPair>();
        string suffix = longText ? new string('a', 2048) : "short";
        for (int i = 0; i < count; i++)
        {
            string text = $"synthetic {(i % 2 == 0 ? "target" : "other")} record {i} {suffix}";
            var data = new ClipboardData(text, DataType.PlainText, false)
            {
                HashId = i.ToString(CultureInfo.InvariantCulture),
                SenderApp = "synthetic.exe",
                InitScore = i,
                CreateTime = new DateTime(2025, 1, 1),
                EncryptKeyMd5 = string.Empty,
                CachedImagePath = string.Empty,
                Pinned = i % 10 == 0,
                Saved = false
            };
            records.AddLast(new ClipboardDataPair
            {
                ClipboardData = data,
                PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must remain lazy"))
            });
        }
        return records;
    }

    private static List<Result> SearchSequential(LinkedList<ClipboardDataPair> records, string search, Func<ClipboardDataPair, string?, Result?> factory)
    {
        var results = new List<Result>();
        foreach (var item in records)
        {
            var result = factory(item, search);
            if (result != null) results.Add(result);
        }
        return results;
    }

    private static List<Result> SearchParallel(LinkedList<ClipboardDataPair> records, string search, Func<ClipboardDataPair, string?, Result?> factory)
    {
        var results = new ConcurrentBag<Result>();
        Parallel.ForEach(records, item =>
        {
            var result = factory(item, search);
            if (result != null) results.Add(result);
        });
        return [.. results];
    }

    private static void VerifyEquivalent(List<Result> first, List<Result> second, int expected)
    {
        if (first.Count != expected || second.Count != expected) throw new InvalidOperationException("Synthetic result count mismatch");
        var expectedById = first.ToDictionary(result => ((ClipboardDataPair)result.ContextData).ClipboardData.HashId);
        foreach (var result in second)
        {
            string id = ((ClipboardDataPair)result.ContextData).ClipboardData.HashId;
            if (!expectedById.Remove(id, out var other) || result.Score != other.Score || result.Title != other.Title ||
                result.SubTitle != other.SubTitle || result.CopyText != other.CopyText)
                throw new InvalidOperationException("Synthetic result content mismatch");
        }
        if (expectedById.Count != 0) throw new InvalidOperationException("Synthetic result identity mismatch");
    }

    private static void MeasurePair(string name, Action serial, Action parallel)
    {
        serial();
        parallel();
        var serialTimes = new double[5];
        var parallelTimes = new double[5];
        var serialBytes = new long[5];
        var parallelBytes = new long[5];
        for (int i = 0; i < serialTimes.Length; i++)
        {
            if (i % 2 == 0)
            {
                Sample(serial, out serialTimes[i], out serialBytes[i]);
                Sample(parallel, out parallelTimes[i], out parallelBytes[i]);
            }
            else
            {
                Sample(parallel, out parallelTimes[i], out parallelBytes[i]);
                Sample(serial, out serialTimes[i], out serialBytes[i]);
            }
        }
        PrintSamples($"{name} serial results", serialTimes, serialBytes);
        PrintSamples($"{name} parallel results", parallelTimes, parallelBytes);
    }

    private static void Sample(Action action, out double elapsed, out long allocated)
    {
        long before = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        action();
        timer.Stop();
        elapsed = timer.Elapsed.TotalMilliseconds;
        allocated = GC.GetTotalAllocatedBytes(true) - before;
    }

    private static void PrintSamples(string name, double[] times, long[] allocations)
    {
        Array.Sort(times);
        Array.Sort(allocations);
        Console.WriteLine($"{name}: ms min/median/max={times[0]:F2}/{times[2]:F2}/{times[4]:F2}; median allocated={allocations[2] / 1048576.0:F2} MiB");
    }

    private static void Measure(string name, Action action)
    {
        action();
        var times = new double[5];
        var allocated = new long[5];
        for (int i = 0; i < times.Length; i++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            action();
            timer.Stop();
            times[i] = timer.Elapsed.TotalMilliseconds;
            allocated[i] = GC.GetTotalAllocatedBytes(true) - before;
        }
        Array.Sort(times);
        Array.Sort(allocated);
        Console.WriteLine($"{name}: ms min/median/max={times[0]:F2}/{times[2]:F2}/{times[4]:F2}; median allocated={allocated[2] / 1048576.0:F2} MiB");
    }
}
