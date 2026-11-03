// PinnedHistoryChangeTracker.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>
/// Compares consecutive trusted event snapshots. Recovery establishes a new
/// baseline instead of treating uncertainty as a Windows unpin event.
/// </summary>
public sealed class PinnedHistoryChangeTracker
{
    private readonly object gate = new();
    private HashSet<string>? baseline;

    public void Invalidate()
    {
        lock (gate) baseline = null;
    }

    public void Reset(IEnumerable<string> pinnedIds, bool trusted)
    {
        lock (gate)
            baseline = trusted ? new HashSet<string>(pinnedIds, StringComparer.OrdinalIgnoreCase) : null;
    }

    // Import reads must recover an invalidated baseline without hiding later events
    // or overwriting a baseline that still needs to detect a pending event.
    public void EstablishBaselineIfMissing(IEnumerable<string> pinnedIds, bool trusted)
    {
        lock (gate)
        {
            if (trusted && baseline is null)
                baseline = new HashSet<string>(pinnedIds, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<(string Id, bool Pinned)> Observe(IEnumerable<string> itemIds, IEnumerable<string> pinnedIds, bool trusted)
    {
        lock (gate)
        {
            if (!trusted)
            {
                baseline = null;
                return [];
            }

            var current = new HashSet<string>(pinnedIds, StringComparer.OrdinalIgnoreCase);
            var changes = baseline is null
                ? Array.Empty<(string Id, bool Pinned)>()
                : itemIds.Where(id => baseline.Contains(id) != current.Contains(id))
                    .Select(id => (Id: id, Pinned: current.Contains(id))).ToArray();
            baseline = current;
            return changes;
        }
    }
}
