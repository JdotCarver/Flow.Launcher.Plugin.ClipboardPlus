// ExactImagePixelComparer.cs
using System;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Conservatively compares frozen, directly comparable 8-bit pixel buffers.</summary>
public static class ExactImagePixelComparer
{
    private const int MaximumBytes = 64 * 1024 * 1024;

    // Opaque BGR formats are comparable with straight-alpha BGRA. Premultiplied
    // alpha remains separate: converting it can discard information by rounding.
    private static (byte[] Pixels, bool Premultiplied)? ReadPixels(BitmapSource? image)
    {
        if (image is null || !image.IsFrozen) return null;
        var format = image.Format;
        if (format != PixelFormats.Bgra32 && format != PixelFormats.Pbgra32 &&
            format != PixelFormats.Bgr32 && format != PixelFormats.Bgr24) return null;
        try
        {
            var count = checked(image.PixelWidth * image.PixelHeight);
            var outputSize = checked(count * 4);
            if (outputSize <= 0 || outputSize > MaximumBytes) return null;
            var inputBpp = format == PixelFormats.Bgr24 ? 3 : 4;
            var stride = checked(image.PixelWidth * inputBpp);
            var input = new byte[checked(stride * image.PixelHeight)];
            image.CopyPixels(input, stride, 0);
            if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32)
                return (input, format == PixelFormats.Pbgra32);
            var result = new byte[outputSize];
            for (var i = 0; i < count; i++)
            {
                result[i * 4] = input[i * inputBpp];
                result[i * 4 + 1] = input[i * inputBpp + 1];
                result[i * 4 + 2] = input[i * inputBpp + 2];
                result[i * 4 + 3] = 255;
            }
            return (result, false);
        }
        catch (Exception) { return null; }
    }

    public static string? Fingerprint(BitmapSource? image)
    {
        var pixels = ReadPixels(image);
        if (pixels is null || image is null) return null;
        return $"{image.PixelWidth}:{image.PixelHeight}:{pixels.Value.Premultiplied}:{Convert.ToHexString(SHA256.HashData(pixels.Value.Pixels))}";
    }

    public static ClipboardContentComparison Compare(BitmapSource? left, BitmapSource? right)
    {
        if (left is null || right is null || !left.IsFrozen || !right.IsFrozen)
            return ClipboardContentComparison.Unavailable;
        if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight)
            return ClipboardContentComparison.Different;
        var a = ReadPixels(left);
        var b = ReadPixels(right);
        if (a is null || b is null || a.Value.Premultiplied != b.Value.Premultiplied)
            return ClipboardContentComparison.Unavailable;
        return a.Value.Pixels.AsSpan().SequenceEqual(b.Value.Pixels)
            ? ClipboardContentComparison.Equal : ClipboardContentComparison.Different;
    }
}
