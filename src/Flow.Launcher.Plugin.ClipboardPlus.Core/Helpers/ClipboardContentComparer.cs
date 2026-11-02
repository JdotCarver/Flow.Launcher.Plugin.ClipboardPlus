// ClipboardContentComparer.cs
using System;
using System.Linq;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>The result of comparing clipboard content without trusting its hash.</summary>
public enum ClipboardContentComparison
{
    Different,
    Equal,
    Unavailable
}

/// <summary>
/// Compares the content used by deduplication. A matching hash is only a candidate:
/// it must never be treated as proof of equality. Callers must revalidate mutable
/// records under their synchronization boundary before deleting anything.
/// </summary>
public static class ClipboardContentComparer
{
    public static ClipboardContentComparison Compare(ClipboardData first, ClipboardData second, bool verifyImages)
    {
        if (first.DataType != second.DataType)
        {
            return ClipboardContentComparison.Different;
        }

        try
        {
            return first.DataType switch
            {
                DataType.PlainText => CompareStrings(first.Data, second.Data),
                DataType.RichText => CompareRichText(first, second),
                DataType.Files => CompareFiles(first.Data, second.Data),
                DataType.Image => CompareImages(first, second, verifyImages),
                _ => ClipboardContentComparison.Unavailable
            };
        }
        catch (Exception)
        {
            // An unreadable or thread-affine image is not evidence for deletion.
            return ClipboardContentComparison.Unavailable;
        }
    }

    private static ClipboardContentComparison CompareStrings(object? first, object? second)
    {
        if (first is not string left || second is not string right)
        {
            return ClipboardContentComparison.Unavailable;
        }
        return Result(string.Equals(left, right, StringComparison.Ordinal));
    }

    private static ClipboardContentComparison CompareRichText(ClipboardData first, ClipboardData second)
    {
        var payload = CompareStrings(first.Data, second.Data);
        if (payload != ClipboardContentComparison.Equal)
        {
            return payload;
        }
        if (first.PlainText is null || second.PlainText is null)
        {
            return ClipboardContentComparison.Unavailable;
        }
        return Result(string.Equals(first.PlainText, second.PlainText, StringComparison.Ordinal));
    }

    private static ClipboardContentComparison CompareFiles(object? first, object? second)
    {
        if (first is not string[] left || second is not string[] right ||
            left.Any(path => path is null) || right.Any(path => path is null))
        {
            return ClipboardContentComparison.Unavailable;
        }
        return Result(left.SequenceEqual(right, StringComparer.Ordinal));
    }

    private static ClipboardContentComparison CompareImages(ClipboardData first, ClipboardData second, bool verifyImages)
    {
        if (!verifyImages || first.Data is not BitmapSource { IsFrozen: true } ||
            second.Data is not BitmapSource { IsFrozen: true })
        {
            return ClipboardContentComparison.Unavailable;
        }
        var left = first.DataToString(false);
        var right = second.DataToString(false);
        return string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)
            ? ClipboardContentComparison.Unavailable
            : Result(string.Equals(left, right, StringComparison.Ordinal));
    }

    private static ClipboardContentComparison Result(bool equal) => equal
        ? ClipboardContentComparison.Equal
        : ClipboardContentComparison.Different;
}
