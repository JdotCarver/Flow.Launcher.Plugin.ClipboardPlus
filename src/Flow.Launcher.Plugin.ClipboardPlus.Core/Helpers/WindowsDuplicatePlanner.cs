// WindowsDuplicatePlanner.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>A snapshot entry; WindowsId is null for plugin-origin records.</summary>
public readonly record struct WindowsDuplicateEntry(ClipboardData Data, string? WindowsId);

/// <summary>A Windows item and the snapshot index of its proposed visible keeper.</summary>
public readonly record struct WindowsDuplicateCandidate(string WindowsId, int KeeperIndex, int WindowsIndex);

/// <summary>
/// Proposes plugin-side hiding only. It never changes Windows history. The
/// caller must revalidate pin state, content and keeper visibility before hiding.
/// </summary>
public static class WindowsDuplicatePlanner
{
    public static IReadOnlyList<WindowsDuplicateCandidate> Plan(IReadOnlyList<WindowsDuplicateEntry> entries, bool verifyImages)
    {
        var result = new List<WindowsDuplicateCandidate>();
        // A repeated ID makes pin and identity state ambiguous. Never hide it,
        // and never rely on it as a keeper for another item.
        var ambiguousIds = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.WindowsId))
            .GroupBy(entry => entry.WindowsId!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var groups = entries.Select((entry, index) => (Entry: entry, Index: index))
            .Where(item => !item.Entry.Data.Pinned && !string.IsNullOrEmpty(item.Entry.Data.DataMd5) &&
                           (item.Entry.WindowsId is null || !ambiguousIds.Contains(item.Entry.WindowsId)))
            .GroupBy(item => (item.Entry.Data.DataType, item.Entry.Data.DataMd5));
        foreach (var group in groups)
        {
            var keepers = new List<(WindowsDuplicateEntry Entry, int Index)>();
            foreach (var item in group.OrderByDescending(item => item.Entry.Data.CreateTime).ThenBy(item => item.Index))
            {
                var matched = false;
                foreach (var keeper in keepers)
                {
                    if (ClipboardContentComparer.Compare(keeper.Entry.Data, item.Entry.Data, verifyImages) != ClipboardContentComparison.Equal)
                    {
                        continue;
                    }
                    matched = true;
                    if (!string.IsNullOrWhiteSpace(item.Entry.WindowsId) &&
                        item.Entry.WindowsId != keeper.Entry.WindowsId)
                    {
                        result.Add(new WindowsDuplicateCandidate(item.Entry.WindowsId, keeper.Index, item.Index));
                    }
                    break;
                }
                if (!matched) keepers.Add(item);
            }
        }
        return result;
    }
}
