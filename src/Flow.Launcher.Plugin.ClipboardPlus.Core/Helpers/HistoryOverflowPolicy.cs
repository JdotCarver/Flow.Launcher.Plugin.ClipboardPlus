// HistoryOverflowPolicy.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Bounds temporary capacity slack while preserving pinned records.</summary>
public static class HistoryOverflowPolicy
{
    // Reviewed with the maintainer against synthetic Windows measurements.
    public const int MaximumOverflow = 25;

    public static int Limit(int configuredLimit, int reserved) =>
        (int)Math.Min(int.MaxValue, (long)Math.Max(0, configuredLimit) + Math.Clamp(reserved, 0, MaximumOverflow));

    /// <summary>
    /// Reserves one slot for each unresolved extra candidate, not for unrelated
    /// skips or intentionally distinct saved and temporary representatives.
    /// Hashes identify possible groups only; they never establish equality.
    /// </summary>
    public static int CountUncertain(IReadOnlyList<ClipboardCleanupSnapshot> snapshots, bool thorough,
        IReadOnlySet<int> failedCandidates)
    {
        var uncertain = new HashSet<int>(failedCandidates.Where(index => index >= 0 && index < snapshots.Count && !snapshots[index].Original.Pinned));
        var entries = snapshots.Select((snapshot, index) => (Data: snapshot.Original, Index: index))
            .Where(item => !item.Data.Pinned).ToArray();
        // A missing hash cannot establish even a candidate group. Reserve for
        // potentially comparable records rather than assume they are unique.
        foreach (var typeGroup in entries.GroupBy(item => item.Data.DataType))
        {
            var members = typeGroup.ToArray();
            if (members.Length > 1)
                foreach (var member in members.Where(item => string.IsNullOrEmpty(item.Data.DataMd5)).Take(members.Length - 1))
                    uncertain.Add(member.Index);
        }
        foreach (var group in entries.Where(item => !string.IsNullOrEmpty(item.Data.DataMd5))
                     .GroupBy(item => (item.Data.DataType, item.Data.DataMd5)))
        {
            var members = group.ToArray();
            if (members.Length < 2) continue;
            if (members.Any(item => !CanVerify(item.Data, thorough)))
                foreach (var member in members.Skip(1)) uncertain.Add(member.Index);
        }
        return Math.Min(MaximumOverflow, uncertain.Count);
    }

    private static bool CanVerify(ClipboardData data, bool thorough) => thorough
        ? data.DataType switch
        {
            DataType.PlainText => data.Data is string,
            DataType.RichText => data.Data is string && data.PlainText is not null,
            DataType.Files => data.Data is string[] paths && paths.All(path => path is not null),
            DataType.Image => data.Data is BitmapSource { IsFrozen: true },
            _ => false
        }
        : DuplicateVerificationPolicy.CanVerifyBestEffort(data);
}
