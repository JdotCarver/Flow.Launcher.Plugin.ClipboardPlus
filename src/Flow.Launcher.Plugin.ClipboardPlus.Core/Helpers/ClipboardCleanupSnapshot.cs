// ClipboardCleanupSnapshot.cs
using System;
using System.Linq;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>
/// Captures the identity, metadata and mutable file-list contents needed to
/// reject stale cleanup plans. Capture and revalidation must be synchronized
/// with the owning history collection.
/// </summary>
public sealed class ClipboardCleanupSnapshot
{
    private readonly ClipboardData original;
    private readonly string[]? filePaths;
    private readonly bool imageWasFrozen;

    public ClipboardCleanupSnapshot(ClipboardData data)
    {
        original = data;
        imageWasFrozen = data.Data is BitmapSource { IsFrozen: true };
        filePaths = data.Data is string[] paths ? (string[])paths.Clone() : null;
    }

    public ClipboardData Original => original;

    /// <summary>Builds a detached planning value without serializing an image.</summary>
    public ClipboardData CreatePlanningRecord()
    {
        if (filePaths is null)
        {
            return original;
        }
        return new ClipboardData((string[])filePaths.Clone(), original.DataType, original.EncryptData)
        {
            HashId = original.HashId, SenderApp = original.SenderApp, InitScore = original.InitScore,
            CreateTime = original.CreateTime, EncryptKeyMd5 = original.EncryptKeyMd5,
            CachedImagePath = original.CachedImagePath, Pinned = original.Pinned,
            Saved = original.Saved, PlainText = original.PlainText
        };
    }

    public bool Matches(ClipboardData current)
    {
        if (current.HashId != original.HashId || current.DataType != original.DataType ||
            current.DataMd5 != original.DataMd5 || current.CreateTime != original.CreateTime ||
            current.SenderApp != original.SenderApp || current.InitScore != original.InitScore ||
            current.EncryptKeyMd5 != original.EncryptKeyMd5 || current.EncryptData != original.EncryptData ||
            current.CachedImagePath != original.CachedImagePath || current.PlainText != original.PlainText ||
            current.Pinned != original.Pinned || current.Saved != original.Saved ||
            current.FromWindowsClipboardHistory() != original.FromWindowsClipboardHistory())
        {
            return false;
        }
        return original.DataType switch
        {
            DataType.PlainText or DataType.RichText => original.Data is string text && current.Data is string now &&
                string.Equals(text, now, StringComparison.Ordinal),
            DataType.Files => filePaths is not null && current.Data is string[] paths &&
                filePaths.SequenceEqual(paths, StringComparer.Ordinal),
            DataType.Image => imageWasFrozen && original.Data is BitmapSource { IsFrozen: true } && ReferenceEquals(original.Data, current.Data),
            _ => false
        };
    }
}
