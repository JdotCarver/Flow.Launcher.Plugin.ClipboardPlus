// ClipboardPlus.WindowsSuppression.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.ClipboardPlus;

public partial class ClipboardPlus
{
    private WindowsSuppressionRegistry windowsSuppression = new();
    private Dictionary<string, WindowsEvidence> windowsEvidence = new(StringComparer.Ordinal);
    private long windowsEvidenceGeneration;
    private bool lastRememberWindowsSuppression;

    private sealed record WindowsEvidence(LinkedListNode<ClipboardDataPair> Target, ClipboardCleanupSnapshot TargetSnapshot,
        LinkedListNode<ClipboardDataPair> Keeper, ClipboardCleanupSnapshot KeeperSnapshot);

    private void InitializeWindowsSuppression()
    {
        lastRememberWindowsSuppression = Settings.RememberWindowsSuppression;
        windowsSuppression = new WindowsSuppressionRegistry(lastRememberWindowsSuppression ? Settings.SuppressedWindowsItems : null);
    }

    // Called while the history lock is held, including parallel query workers.
    private bool IsWindowsSuppressed(ClipboardDataPair pair)
    {
        var data = pair.ClipboardData;
        if (!data.FromWindowsClipboardHistory() || !WindowsClipboardHelper.IsPinStateTrusted ||
            !windowsEvidence.TryGetValue(data.HashId, out var evidence)) return false;
        var verified = evidence.Target.List == RecordsList && evidence.Keeper.List == RecordsList &&
            ReferenceEquals(evidence.Target.Value, pair) &&
            evidence.TargetSnapshot.Matches(data) && evidence.KeeperSnapshot.Matches(evidence.Keeper.Value.ClipboardData) &&
            !evidence.Keeper.Value.ClipboardData.Pinned;
        return windowsSuppression.ShouldHide(data.HashId, data.Pinned, Settings.AutomaticCleanup, verified);
    }

    private void RequestWindowsEvidenceRefresh()
    {
        Interlocked.Increment(ref windowsEvidenceGeneration);
        if (Settings is null) return;
        if (Settings.AutomaticCleanup) ScheduleAutomaticCleanup();
        else if (windowsSuppression.GetManualIds().Count > 0) cleanupCoordinator.RequestWindowsEvidence();
    }

    private void WindowsClipboardHelper_OnHistoryReadCompleted(object? sender, string[] ids)
    {
        var previous = windowsSuppression.GetPersistentEntries();
        windowsSuppression.PruneAbsent(ids, true);
        if (!previous.SequenceEqual(windowsSuppression.GetPersistentEntries())) SaveWindowsSuppression();
        RequestWindowsEvidenceRefresh();
    }

    private void SaveWindowsSuppression()
    {
        if (Settings is null || !Settings.RememberWindowsSuppression) return;
        try
        {
            Settings.SuppressedWindowsItems = windowsSuppression.GetPersistentEntries().ToList();
            // Settings storage is owned by Flow; avoid logging IDs or clipboard data.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Context?.API.SaveSettingJsonStorage<Settings>();
            else _ = dispatcher.InvokeAsync(() => Context?.API.SaveSettingJsonStorage<Settings>());
        }
        catch (Exception)
        {
            // Suppression fails open after a reload if IDs could not be saved.
        }
    }

    private async Task<CleanupReport> RefreshWindowsEvidenceAsync(CleanupWork? work, bool thorough, long generation)
    {
        LinkedListNode<ClipboardDataPair>[] nodes;
        ClipboardCleanupSnapshot[] snapshots;
        await RecordsLock.WaitAsync();
        try
        {
            var list = new List<LinkedListNode<ClipboardDataPair>>(RecordsList.Count);
            for (var node = RecordsList.First; node is not null; node = node.Next) list.Add(node);
            nodes = list.ToArray();
            snapshots = nodes.Select(node => new ClipboardCleanupSnapshot(node.Value.ClipboardData)).ToArray();
        }
        finally { RecordsLock.Release(); }

        var indexed = snapshots.Select((snapshot, index) => (Index: index, Entry: new WindowsDuplicateEntry(snapshot.CreatePlanningRecord(),
            snapshot.Original.FromWindowsClipboardHistory() ? snapshot.Original.HashId : null))).ToArray();
        if (work is null)
        {
            // Reverify only groups containing a manually suppressed ID; do not
            // encode unrelated images when automatic cleanup is disabled.
            var manualIds = new HashSet<string>(windowsSuppression.GetManualIds(), StringComparer.Ordinal);
            var groups = indexed.Where(item => item.Entry.WindowsId is not null && manualIds.Contains(item.Entry.WindowsId))
                .Select(item => (item.Entry.Data.DataType, item.Entry.Data.DataMd5)).ToHashSet();
            indexed = indexed.Where(item => groups.Contains((item.Entry.Data.DataType, item.Entry.Data.DataMd5))).ToArray();
        }
        var candidates = WindowsDuplicatePlanner.Plan(indexed.Select(item => item.Entry).ToArray(), thorough);
        var evidence = new Dictionary<string, WindowsEvidence>(StringComparer.Ordinal);
        var hidden = 0;
        var skipped = 0;
        await RecordsLock.WaitAsync();
        try
        {
            if (generation != Interlocked.Read(ref windowsEvidenceGeneration)) return new CleanupReport(0, 0, 0, candidates.Count, 0);
            foreach (var candidate in candidates)
            {
                var targetIndex = indexed[candidate.WindowsIndex].Index;
                var keeperIndex = indexed[candidate.KeeperIndex].Index;
                var target = nodes[targetIndex];
                var keeper = nodes[keeperIndex];
                if (!WindowsClipboardHelper.IsPinStateTrusted || target.List != RecordsList || keeper.List != RecordsList ||
                    !snapshots[targetIndex].Matches(target.Value.ClipboardData) ||
                    !snapshots[keeperIndex].Matches(keeper.Value.ClipboardData))
                {
                    skipped++;
                    continue;
                }
                var wasHidden = IsWindowsSuppressed(target.Value);
                if (work == CleanupWork.ManualThorough || work == CleanupWork.ManualExtrasOnly)
                    windowsSuppression.RememberManual(candidate.WindowsId, Settings.RememberWindowsSuppression);
                else if ((work == CleanupWork.AutomaticBestEffort || work == CleanupWork.AutomaticThorough) && Settings.AutomaticCleanup)
                    windowsSuppression.RememberAutomatic(candidate.WindowsId, Settings.RememberWindowsSuppression);
                evidence[candidate.WindowsId] = new WindowsEvidence(target, snapshots[targetIndex], keeper, snapshots[keeperIndex]);
                if (!wasHidden && windowsSuppression.ShouldHide(candidate.WindowsId, false, Settings.AutomaticCleanup, true)) hidden++;
            }
            windowsEvidence = evidence;
        }
        finally { RecordsLock.Release(); }
        if (work is not null) SaveWindowsSuppression();
        return new CleanupReport(0, 0, hidden, skipped, 0);
    }
}
