// ExactImagePixelComparerTests.cs
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class ExactImagePixelComparerTests
{
    private static BitmapSource Image(byte[] pixels, double dpi = 96)
    {
        var image = BitmapSource.Create(1, 1, dpi, dpi, PixelFormats.Bgra32, null, pixels, 4);
        image.Freeze();
        return image;
    }

    [Fact]
    public void EqualPixelsAreIndependentOfDpiButDifferentPixelsAreNotEqual()
    {
        var first = Image([1, 2, 3, 255]);
        var second = Image([1, 2, 3, 255], 144);
        var different = Image([1, 2, 4, 255]);
        Assert.Equal(ClipboardContentComparison.Equal, ExactImagePixelComparer.Compare(first, second));
        Assert.Equal(ClipboardContentComparison.Different, ExactImagePixelComparer.Compare(first, different));
        Assert.Equal(ClipboardContentComparison.Unavailable, ExactImagePixelComparer.Compare(first, null));
    }
}
