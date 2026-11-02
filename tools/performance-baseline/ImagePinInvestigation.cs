// ImagePinInvestigation.cs
// Measures synthetic pin toggles without invoking clipboard or real database operations.
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;

internal static class ImagePinInvestigation
{
    public static void Run()
    {
        Measure("image pin 512 solid", CreatePair(CreateImage(512, false), DataType.Image));
        Measure("image pin 512 noise", CreatePair(CreateImage(512, true), DataType.Image));
        Measure("image pin 1024 noise", CreatePair(CreateImage(1024, true), DataType.Image));
        Measure("image pin text control", CreatePair("synthetic text", DataType.PlainText));
        Console.WriteLine("image pin identity and state checks passed");
    }

    private static BitmapSource CreateImage(int size, bool noisy)
    {
        var pixels = new byte[size * size * 4];
        if (noisy) new Random(123).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        image.Freeze();
        return image;
    }

    private static ClipboardDataPair CreatePair(object content, DataType type)
    {
        var record = new ClipboardData(content, type, false)
        {
            HashId = "synthetic",
            SenderApp = "synthetic.exe",
            InitScore = 1,
            CreateTime = new DateTime(2025, 1, 1),
            EncryptKeyMd5 = string.Empty,
            CachedImagePath = string.Empty,
            Pinned = false,
            Saved = true
        };
        return new ClipboardDataPair { ClipboardData = record, PreviewPanel = new Lazy<UserControl>(() => throw new InvalidOperationException("Preview must stay lazy")) };
    }

    private static void Measure(string label, ClipboardDataPair pair)
    {
        var initial = pair.ClipboardData;
        pair.TogglePinned();
        if (!pair.ClipboardData.Pinned || pair.ClipboardData.HashId != initial.HashId ||
            pair.ClipboardData.DataMd5 != initial.DataMd5 || !ReferenceEquals(pair.ClipboardData.Data, initial.Data))
            throw new InvalidOperationException("Synthetic pin identity mismatch");
        var durations = new double[5];
        var allocations = new long[5];
        for (int i = 0; i < durations.Length; i++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            pair.TogglePinned();
            timer.Stop();
            durations[i] = timer.Elapsed.TotalMilliseconds;
            allocations[i] = GC.GetTotalAllocatedBytes(true) - before;
        }
        if (pair.ClipboardData.Pinned || pair.ClipboardData.HashId != initial.HashId || pair.ClipboardData.DataMd5 != initial.DataMd5)
            throw new InvalidOperationException("Synthetic pin toggle mismatch");
        Array.Sort(durations);
        Array.Sort(allocations);
        Console.WriteLine($"{label}: ms min/median/max={durations[0]:F2}/{durations[2]:F2}/{durations[4]:F2}; median allocated={allocations[2] / 1048576.0:F2} MiB");
    }
}
