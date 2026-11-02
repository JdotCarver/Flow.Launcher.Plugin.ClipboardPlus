// ClipboardContentComparerTests.cs
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class ClipboardContentComparerTests
{
    private static ClipboardData Record(object? data, DataType type, string plainText = "") => new(data, type, false)
    {
        HashId = Guid.NewGuid().ToString(), SenderApp = "synthetic", InitScore = 0,
        CreateTime = new DateTime(2026, 1, 1), Pinned = false, Saved = false, PlainText = plainText
    };

    [Fact]
    public void TextAndRichTextRequireExactContentAndType()
    {
        var text = Record("hello", DataType.PlainText);
        Assert.Equal(ClipboardContentComparison.Equal, ClipboardContentComparer.Compare(text, Record("hello", DataType.PlainText), true));
        Assert.Equal(ClipboardContentComparison.Different, ClipboardContentComparer.Compare(text, Record("Hello", DataType.PlainText), true));
        Assert.Equal(ClipboardContentComparison.Different, ClipboardContentComparer.Compare(text, Record("hello", DataType.RichText), true));
        var rich = Record(@"{\rtf1 test}", DataType.RichText, "test");
        Assert.Equal(ClipboardContentComparison.Different, ClipboardContentComparer.Compare(rich, Record(@"{\rtf1 test}", DataType.RichText, "other"), true));
        Assert.Equal(ClipboardContentComparison.Equal, ClipboardContentComparer.Compare(rich, Record(@"{\rtf1 test}", DataType.RichText, "test"), true));
    }

    [Fact]
    public void FileHashCollisionDoesNotProveEquality()
    {
        var first = Record(new[] { "a\nb", "c" }, DataType.Files);
        var second = Record(new[] { "a", "b\nc" }, DataType.Files);
        Assert.Equal(first.DataMd5, second.DataMd5);
        Assert.Equal(ClipboardContentComparison.Different, ClipboardContentComparer.Compare(first, second, true));
        Assert.Equal(ClipboardContentComparison.Different, ClipboardContentComparer.Compare(
            Record(new[] { "a", "b" }, DataType.Files), Record(new[] { "b", "a" }, DataType.Files), true));
        Assert.Equal(ClipboardContentComparison.Equal, ClipboardContentComparer.Compare(
            Record(new[] { "a", "b" }, DataType.Files), Record(new[] { "a", "b" }, DataType.Files), true));
    }

    [Fact]
    public void ImageVerificationRequiresFrozenAccessibleImagesAndExplicitPermission()
    {
        var image = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        var mutable = Record(image, DataType.Image);
        Assert.Equal(ClipboardContentComparison.Unavailable, ClipboardContentComparer.Compare(mutable, mutable, true));
        image.Freeze();
        var frozen = Record(image, DataType.Image);
        Assert.Equal(ClipboardContentComparison.Unavailable, ClipboardContentComparer.Compare(frozen, frozen, false));
        Assert.Equal(ClipboardContentComparison.Equal, ClipboardContentComparer.Compare(frozen, frozen, true));
    }

    [Fact]
    public void UnknownAndMalformedContentIsNotEvidenceForDeletion()
    {
        Assert.Equal(ClipboardContentComparison.Unavailable, ClipboardContentComparer.Compare(ClipboardData.NULL, ClipboardData.NULL, true));
        Assert.Equal(ClipboardContentComparison.Unavailable, ClipboardContentComparer.Compare(
            Record(null, DataType.PlainText), Record(null, DataType.PlainText), true));
    }
}
