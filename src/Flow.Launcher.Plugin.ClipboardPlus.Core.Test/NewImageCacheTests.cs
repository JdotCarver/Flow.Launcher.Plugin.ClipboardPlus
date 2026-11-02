// NewImageCacheTests.cs
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class NewImageCacheTests
{
    [Fact]
    public void NewlyCreatedCacheImagesNeverOverwriteExistingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"clipboardplus-new-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var original = Path.Combine(root, "synthetic.png");
            File.WriteAllBytes(original, new byte[] { 1, 2, 3 });
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 5, 6, 7, 255 }, 4);
            image.Freeze();
            var record = new ClipboardData(image, DataType.Image, false)
            {
                HashId = "synthetic", SenderApp = "synthetic", InitScore = 0,
                CreateTime = new DateTime(2026, 1, 1), Pinned = false, Saved = false
            };
            var created = FileUtils.SaveNewImageCache(record, root, "synthetic", out var ownership);
            Assert.NotEqual(original, created);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(original));
            Assert.True(File.Exists(created));
            Assert.NotNull(ownership);
            Assert.Equal(Path.GetFileName(created), ownership.FileName);
            Assert.Equal(string.Empty, FileUtils.SaveNewImageCache(record, root, "../unsafe", out _));
        }
        finally { Directory.Delete(root, true); }
    }
}
