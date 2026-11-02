// ImageExperiment.cs
// Tests synthetic image encoding strategies without reading the clipboard or user files.
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Enums;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Extensions;
using Flow.Launcher.Plugin.ClipboardPlus.Core.Utils;

internal static class ImageExperiment
{
    public static void Run()
    {
        foreach (var (size, noisy) in new[] { (512, false), (512, true), (1024, true) })
        {
            var image = CreateImage(size, noisy);
            string description = $"{size} {(noisy ? "noise" : "solid")}";
            string encoded = image.ToBase64();
            var plainRecord = CreateRecord(image, false);
            var encryptedRecord = CreateRecord(image, true);
            // Reuse must produce exactly the existing hash and stored data.
            if (plainRecord.DataMd5 != StringUtils.GetMd5(encoded) ||
                Asset.FromClipboardData(plainRecord, true).DataB64 != encoded ||
                Asset.FromClipboardData(encryptedRecord, true).DataB64 != StringUtils.Encrypt(encoded, StringUtils.EncryptKey))
                throw new InvalidOperationException("Synthetic encoded representation mismatch");
            var decoded = encoded.ToBitmapImage() ?? throw new InvalidOperationException("Synthetic decode failed");
            if (decoded.PixelWidth != size || decoded.PixelHeight != size || !PixelsEqual(image, decoded))
                throw new InvalidOperationException("Synthetic image pixels changed");

            Measure($"image experiment {description} png base64", () => { _ = image.ToBase64(); });
            Measure($"image experiment {description} hash encoded", () => { _ = StringUtils.GetMd5(encoded); });
            Measure($"image experiment {description} encrypt encoded", () => { _ = StringUtils.Encrypt(encoded, StringUtils.EncryptKey); });
            Measure($"image experiment {description} current hash and asset", () =>
            {
                var record = CreateRecord(image, false);
                _ = Asset.FromClipboardData(record, true);
            });
            Measure($"image experiment {description} reuse hash and asset", () =>
            {
                var once = image.ToBase64();
                _ = StringUtils.GetMd5(once);
                GC.KeepAlive(once);
            });
            Measure($"image experiment {description} current encrypted hash and asset", () =>
            {
                var record = CreateRecord(image, true);
                _ = Asset.FromClipboardData(record, true);
            });
            Measure($"image experiment {description} reuse encrypted hash and asset", () =>
            {
                var once = image.ToBase64();
                _ = StringUtils.GetMd5(once);
                _ = StringUtils.Encrypt(once, StringUtils.EncryptKey);
            });
            if (size == 512 && noisy) MeasureCache(image, encoded);
        }
        Console.WriteLine("image experiment representation and pixel checks passed");
    }

    private static void MeasureCache(BitmapSource image, string encoded)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"clipboardplus-images-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string candidatePath = Path.Combine(directory, "candidate.png");
        try
        {
            var record = CreateRecord(image, false);
            string currentPath = FileUtils.SaveImageCache(record, directory, "current");
            if (string.IsNullOrEmpty(currentPath) || !File.ReadAllBytes(currentPath).SequenceEqual(Convert.FromBase64String(encoded)))
                throw new InvalidOperationException("Synthetic cached PNG mismatch");
            Measure("image experiment 512 noise current hash and cache", () =>
            {
                var current = CreateRecord(image, false);
                if (string.IsNullOrEmpty(FileUtils.SaveImageCache(current, directory, "current")))
                    throw new InvalidOperationException("Synthetic cache write failed");
            });
            Measure("image experiment 512 noise reuse hash and cache", () =>
            {
                var once = image.ToBase64();
                _ = StringUtils.GetMd5(once);
                File.WriteAllBytes(candidatePath, Convert.FromBase64String(once));
            });
            if (!File.ReadAllBytes(candidatePath).SequenceEqual(File.ReadAllBytes(currentPath)))
                throw new InvalidOperationException("Synthetic cache candidate mismatch");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static BitmapSource CreateImage(int size, bool noisy)
    {
        var bytes = new byte[size * size * 4];
        if (noisy) new Random(123).NextBytes(bytes);
        // Opaque pixels are representative of screenshots and avoid undefined RGB under zero alpha.
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bytes, size * 4);
        image.Freeze();
        return image;
    }

    private static bool PixelsEqual(BitmapSource first, BitmapSource second)
    {
        var left = new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0);
        var right = new FormatConvertedBitmap(second, PixelFormats.Bgra32, null, 0);
        int stride = first.PixelWidth * 4;
        var leftBytes = new byte[stride * first.PixelHeight];
        var rightBytes = new byte[leftBytes.Length];
        left.CopyPixels(leftBytes, stride, 0);
        right.CopyPixels(rightBytes, stride, 0);
        return leftBytes.SequenceEqual(rightBytes);
    }

    private static ClipboardData CreateRecord(BitmapSource image, bool encrypt) => new(image, DataType.Image, encrypt)
    {
        HashId = "synthetic",
        SenderApp = "synthetic.exe",
        InitScore = 0,
        CreateTime = new DateTime(2025, 1, 1),
        EncryptKeyMd5 = StringUtils.EncryptKeyMd5,
        CachedImagePath = string.Empty,
        Pinned = false,
        Saved = true
    };

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
