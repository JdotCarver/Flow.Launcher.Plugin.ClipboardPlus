// DeduplicationPlannerBenchmark.cs
// Uses synthetic in-memory records only; no clipboard, settings, cache or database.
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

internal static class DeduplicationPlannerBenchmark
{
    public static void Run()
    {
        foreach (int count in new[] { 1000, 10000 })
        {
            var records = Enumerable.Range(0, count)
                .Select(i => Record($"synthetic-{i % 500}-" + new string('x', 512), DataType.PlainText, i))
                .ToArray();
            Measure($"dedup planner {count} text best effort", records, false, count - 500);
            Measure($"dedup planner {count} text thorough", records, true, count - 500);
        }

        const int size = 1024;
        var pixels = new byte[size * size * 4];
        new Random(42).NextBytes(pixels);
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        image.Freeze();
        var images = Enumerable.Range(0, 4).Select(i => Record(image, DataType.Image, i)).ToArray();
        Measure("dedup planner 4 noisy 1024 images best effort", images, false, 0);
        Measure("dedup planner 4 noisy 1024 images thorough", images, true, 3);
    }

    private static void Measure(string label, ClipboardData[] records, bool thorough, int expected)
    {
        for (int i = 0; i < 4; i++)
        {
            var timer = Stopwatch.StartNew();
            var plan = DuplicateRecordPlanner.Plan(records, thorough);
            timer.Stop();
            if (plan.Count != expected) throw new InvalidOperationException("Synthetic duplicate count mismatch");
            if (i > 0) samples[i - 1] = timer.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        Console.WriteLine($"{label}: ms min/median/max={samples[0]:F2}/{samples[1]:F2}/{samples[2]:F2}");
    }

    private static readonly double[] samples = new double[3];

    private static ClipboardData Record(object content, DataType type, int index) => new(content, type, false)
    {
        HashId = $"synthetic-{index}", SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1).AddSeconds(index), Pinned = false, Saved = true
    };
}
