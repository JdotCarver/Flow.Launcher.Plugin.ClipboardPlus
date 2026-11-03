// ExactImagePixelComparer.cs
using System;
using System.Windows.Media;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Conservatively compares frozen, directly comparable pixel buffers.</summary>
public static class ExactImagePixelComparer
{
    // Bound memory consumed by a single comparison; an oversized image is unknown, not equal.
    private const int MaximumBytes = 64 * 1024 * 1024;

    public static string? Fingerprint(BitmapSource? image)
    {
        if (image is null || !image.IsFrozen ||
            (image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Bgr32 && image.Format != PixelFormats.Bgr24)) return null;
        try
        {
            var stride = checked(image.PixelWidth * image.Format.BitsPerPixel / 8);
            var size = checked(stride * image.PixelHeight);
            if (size <= 0 || size > MaximumBytes) return null;
            var pixels = new byte[size];
            image.CopyPixels(pixels, stride, 0);
            return $"{image.PixelWidth}:{image.PixelHeight}:{image.Format}:{Convert.ToHexString(SHA256.HashData(pixels))}";
        }
        catch (Exception) { return null; }
    }

    public static ClipboardContentComparison Compare(BitmapSource? left, BitmapSource? right)
    {
        if (left is null || right is null || !left.IsFrozen || !right.IsFrozen)
            return ClipboardContentComparison.Unavailable;
        if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight)
            return ClipboardContentComparison.Different;
        // Do not equate raw indices, color-managed data, or different pixel formats.
        if (left.Format != right.Format || (left.Format != PixelFormats.Bgra32 && left.Format != PixelFormats.Bgr32 && left.Format != PixelFormats.Bgr24))
            return ClipboardContentComparison.Unavailable;
        try
        {
            var stride = checked(left.PixelWidth * left.Format.BitsPerPixel / 8);
            var size = checked(stride * left.PixelHeight);
            if (size <= 0 || size > MaximumBytes) return ClipboardContentComparison.Unavailable;
            var a = new byte[size];
            var b = new byte[size];
            left.CopyPixels(a, stride, 0);
            right.CopyPixels(b, stride, 0);
            return a.AsSpan().SequenceEqual(b) ? ClipboardContentComparison.Equal : ClipboardContentComparison.Different;
        }
        catch (Exception) { return ClipboardContentComparison.Unavailable; }
    }
}
