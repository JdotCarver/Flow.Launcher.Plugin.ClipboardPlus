// WindowsSuppressionRegistry.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Only Windows item IDs and their suppression origin are persisted.</summary>
public sealed record WindowsSuppressionEntry(string Id, bool Automatic, bool Manual);

/// <summary>
/// Tracks plugin-side suppression only. It never modifies Windows history and
/// never stores clipboard content or hashes. The caller must independently
/// verify an eligible equivalent is still visible every time it hides an item.
/// </summary>
public sealed class WindowsSuppressionRegistry
{
    private readonly object sync = new();
    private readonly HashSet<string> automaticSession = new(StringComparer.Ordinal);
    private readonly HashSet<string> manualSession = new(StringComparer.Ordinal);
    private readonly HashSet<string> automaticPersistent = new(StringComparer.Ordinal);
    private readonly HashSet<string> manualPersistent = new(StringComparer.Ordinal);

    public WindowsSuppressionRegistry(IEnumerable<WindowsSuppressionEntry>? entries = null)
    {
        if (entries is null) return;
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id)) continue;
            if (entry.Automatic) automaticPersistent.Add(entry.Id);
            if (entry.Manual) manualPersistent.Add(entry.Id);
        }
    }

    public void RememberAutomatic(string id, bool persist) => Remember(id, persist, automaticSession, automaticPersistent);

    public void RememberManual(string id, bool persist) => Remember(id, persist, manualSession, manualPersistent);

    private void Remember(string id, bool persist, HashSet<string> session, HashSet<string> persistent)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An item ID is required.", nameof(id));
        lock (sync)
        {
            (persist ? persistent : session).Add(id);
        }
    }

    public bool ShouldHide(string id, bool pinned, bool automaticEnabled, bool verifiedVisibleEquivalent)
    {
        if (pinned || !verifiedVisibleEquivalent || string.IsNullOrWhiteSpace(id)) return false;
        lock (sync)
        {
            return manualSession.Contains(id) || manualPersistent.Contains(id) ||
                (automaticEnabled && (automaticSession.Contains(id) || automaticPersistent.Contains(id)));
        }
    }

    /// <summary>Keep existing suppression for this session, but stop persisting it.</summary>
    public void ForgetPersistence()
    {
        lock (sync)
        {
            automaticSession.UnionWith(automaticPersistent);
            manualSession.UnionWith(manualPersistent);
            automaticPersistent.Clear();
            manualPersistent.Clear();
        }
    }

    /// <summary>Incomplete or failed reads must never prune IDs.</summary>
    public void PruneAbsent(IEnumerable<string> presentIds, bool completeRead)
    {
        if (!completeRead) return;
        var present = new HashSet<string>(presentIds, StringComparer.Ordinal);
        lock (sync)
        {
            automaticSession.IntersectWith(present);
            manualSession.IntersectWith(present);
            automaticPersistent.IntersectWith(present);
            manualPersistent.IntersectWith(present);
        }
    }

    public IReadOnlyList<WindowsSuppressionEntry> GetPersistentEntries()
    {
        lock (sync)
        {
            return automaticPersistent.Union(manualPersistent, StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => new WindowsSuppressionEntry(id, automaticPersistent.Contains(id), manualPersistent.Contains(id)))
                .ToArray();
        }
    }
}
