// ClipboardPlus.Cleanup.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace Flow.Launcher.Plugin.ClipboardPlus;

public partial class ClipboardPlus
{
    private readonly CleanupRunCoordinator cleanupCoordinator;
    private bool lastAutomaticCleanup;
    private bool lastAutomaticCleanupThorough;

    public ManualCleanupRequest RequestManualCleanup(IProgress<CleanupReport>? progress = null) => cleanupCoordinator.RequestManual(progress);

    private long cleanupQueryGeneration;

    private Result CreateCleanupAction(List<Result> results, Query query, CancellationToken token, long generation)
    {
        Result action = null!;
        void Publish()
        {
            if (!token.IsCancellationRequested && Interlocked.Read(ref cleanupQueryGeneration) == generation)
            {
                ResultsUpdated?.Invoke(this, new ResultUpdatedEventArgs { Results = results, Query = query });
            }
        }
        var finished = false;
        async Task CompleteAsync(Task<CleanupReport> task)
        {
            var report = await task;
            finished = true;
            action.SubTitle = Localize.flowlauncher_plugin_clipboardplus_cleanup_counts(report.RecordsRemoved, report.CacheFilesRemoved, report.WindowsItemsHidden);
            if (report.Skipped > 0) action.SubTitle += " " + Localize.flowlauncher_plugin_clipboardplus_cleanup_skipped(report.Skipped);
            if (report.Errors > 0) action.SubTitle += " " + Localize.flowlauncher_plugin_clipboardplus_cleanup_errors(report.Errors);
            Publish();
        }
        action = new Result
        {
            Title = Localize.flowlauncher_plugin_clipboardplus_cleanup_action(),
            SubTitle = Localize.flowlauncher_plugin_clipboardplus_cleanup_action_subtitle(),
            IcoPath = PathHelper.ClearIconPath,
            Glyph = ResourceHelper.ClearGlyph,
            Score = int.MaxValue,
            AddSelectedCount = false,
            Action = context =>
            {
                finished = false;
                var progress = new Progress<CleanupReport>(report =>
                {
                    if (finished) return;
                    action.SubTitle = Localize.flowlauncher_plugin_clipboardplus_cleanup_running() + " " +
                        Localize.flowlauncher_plugin_clipboardplus_cleanup_counts(report.RecordsRemoved, report.CacheFilesRemoved, report.WindowsItemsHidden);
                    Publish();
                });
                var request = RequestManualCleanup(progress);
                action.SubTitle = request.AlreadyRunning
                    ? Localize.flowlauncher_plugin_clipboardplus_cleanup_already_running()
                    : Localize.flowlauncher_plugin_clipboardplus_cleanup_running();
                Publish();
                _ = CompleteAsync(request.Completion);
                return false;
            }
        };
        return action;
    }

    private void RefreshAutomaticCleanup()
    {
        if (Settings is null) return;
        var enabled = Settings.AutomaticCleanup;
        var thorough = Settings.AutomaticCleanupThorough;
        if (lastRememberWindowsSuppression && !Settings.RememberWindowsSuppression)
        {
            windowsSuppression.ForgetPersistence();
            Settings.SuppressedWindowsItems = [];
        }
        lastRememberWindowsSuppression = Settings.RememberWindowsSuppression;
        cleanupCoordinator.SetAutomaticEnabled(enabled);
        if (enabled && (!lastAutomaticCleanup || lastAutomaticCleanupThorough != thorough))
        {
            cleanupCoordinator.RequestAutomatic(thorough);
        }
        lastAutomaticCleanup = enabled;
        lastAutomaticCleanupThorough = thorough;
    }

    private void ScheduleAutomaticCleanup()
    {
        if (Settings is not null && Settings.AutomaticCleanup)
        {
            cleanupCoordinator.RequestAutomatic(Settings.AutomaticCleanupThorough);
        }
    }

    private async Task<CleanupReport> RunCleanupWorkAsync(CleanupWork work)
    {
        if (work == CleanupWork.WindowsEvidence)
            return await RefreshWindowsEvidenceAsync(null, true, Interlocked.Read(ref windowsEvidenceGeneration));
        if (work == CleanupWork.ManualExtrasOnly)
        {
            var windowsOnly = await RefreshWindowsEvidenceAsync(work, true, Interlocked.Increment(ref windowsEvidenceGeneration));
            windowsOnly += await CleanupRemovedWindowsCacheAsync(true);
            cleanupCoordinator.ReportProgress(windowsOnly);
            return windowsOnly + await ScanOrphanCacheDuplicatesAsync(scan => cleanupCoordinator.ReportProgress(windowsOnly + scan));
        }
        var automatic = work is CleanupWork.AutomaticBestEffort or CleanupWork.AutomaticThorough;
        var thorough = work is CleanupWork.AutomaticThorough or CleanupWork.ManualThorough;
        LinkedListNode<ClipboardDataPair>[] nodes;
        ClipboardCleanupSnapshot[] snapshots;
        long capturedGeneration;
        await RecordsLock.WaitAsync();
        try
        {
            var list = new List<LinkedListNode<ClipboardDataPair>>(RecordsList.Count);
            for (var node = RecordsList.First; node is not null; node = node.Next) list.Add(node);
            nodes = list.ToArray();
            capturedGeneration = captureGeneration;
            snapshots = nodes.Select(node => new ClipboardCleanupSnapshot(node.Value.ClipboardData)).ToArray();
        }
        finally
        {
            RecordsLock.Release();
        }

        // Database-only records participate alongside the loaded history, but a
        // live record always wins when the same ID appears in both snapshots.
        var allNodes = nodes.Cast<LinkedListNode<ClipboardDataPair>?>().ToList();
        var allSnapshots = snapshots.ToList();
        var databaseSkipped = 0;
        if (!UseWindowsClipboardHistoryOnly)
        {
            var stored = await Database.GetCleanupRecordsAsync(thorough);
            databaseSkipped = stored.Skipped;
            var loadedIds = new HashSet<string>(snapshots.Select(snapshot => snapshot.Original.HashId), StringComparer.Ordinal);
            foreach (var data in stored.Records)
            {
                if (loadedIds.Contains(data.HashId)) continue;
                allNodes.Add(null);
                allSnapshots.Add(new ClipboardCleanupSnapshot(data));
            }
        }
        var cacheCandidates = new List<(string Path, string KeeperPath, LinkedListNode<ClipboardDataPair> Keeper, ClipboardCleanupSnapshot Snapshot)>();
        var report = await DuplicateCleanupEngine.RunAsync(allSnapshots, thorough, async (keeperIndex, duplicateIndex) =>
        {
            var keeperSnapshot = allSnapshots[keeperIndex];
            var duplicateSnapshot = allSnapshots[duplicateIndex];
            var keeper = keeperSnapshot.CreatePlanningRecord();
            var duplicate = duplicateSnapshot.CreatePlanningRecord();
            PreparedDuplicateDeletion? prepared = null;
            if (duplicate.Saved)
            {
                if (!keeper.Saved) return DuplicateRemovalResult.Skipped;
                prepared = await Database.PrepareDuplicateDeletionAsync(keeper, duplicate, thorough);
                if (prepared is null) return DuplicateRemovalResult.Skipped;
            }

            ClipboardDataPair? removed = null;
            await RecordsLock.WaitAsync();
            try
            {
                var keeperNode = allNodes[keeperIndex];
                var duplicateNode = allNodes[duplicateIndex];
                bool StillValid(LinkedListNode<ClipboardDataPair>? node, ClipboardCleanupSnapshot snapshot) =>
                    node is null
                        ? !RecordsList.Any(pair => pair.ClipboardData.HashId == snapshot.Original.HashId)
                        : node.List == RecordsList && snapshot.Matches(node.Value.ClipboardData);
                if ((automatic && !Settings.AutomaticCleanup) || (duplicate.Saved && UseWindowsClipboardHistoryOnly) ||
                    !StillValid(keeperNode, keeperSnapshot) || !StillValid(duplicateNode, duplicateSnapshot))
                {
                    return DuplicateRemovalResult.Skipped;
                }
                if (prepared is not null && !await Database.CommitPreparedDuplicateDeletionAsync(prepared))
                {
                    return DuplicateRemovalResult.Skipped;
                }
                if (duplicateNode is not null)
                {
                    removed = duplicateNode.Value;
                    RecordsList.Remove(duplicateNode);
                }
                if (duplicate.DataType == DataType.Image && !string.IsNullOrEmpty(duplicate.CachedImagePath) && keeperNode is not null)
                    cacheCandidates.Add((duplicate.CachedImagePath, keeper.CachedImagePath, keeperNode, keeperSnapshot));
            }
            finally
            {
                RecordsLock.Release();
            }

            // An already-open preview belongs to its WPF dispatcher. Disposing
            // it is not part of the database transaction and must not undo a
            // successful removal if its window has already closed.
            try
            {
                if (removed is null) { }
                else if (removed.PreviewPanel.IsValueCreated)
                {
                    await removed.PreviewPanel.Value.Dispatcher.InvokeAsync(removed.Dispose);
                }
                else
                {
                    removed.Dispose();
                }
            }
            catch (Exception)
            {
                // Clipboard contents and file paths must never be logged here.
            }
            await Task.Yield();
            return DuplicateRemovalResult.Removed;
        }, automatic ? null : cleanupCoordinator.ReportProgress);

        report += new CleanupReport(0, 0, 0, databaseSkipped, 0);
        if (report.RecordsRemoved > 0)
        {
            await RecordsLock.WaitAsync();
            try { ScoreHelper.Reset(RecordsList); }
            finally { RecordsLock.Release(); }
        }
        foreach (var cache in cacheCandidates)
        {
            report += await DeleteRedundantCacheFileAsync(cache.Path, cache.KeeperPath,
                () => cache.Keeper.List == RecordsList && cache.Snapshot.Matches(cache.Keeper.Value.ClipboardData), automatic);
            if (!automatic) cleanupCoordinator.ReportProgress(report);
        }
        var windows = await RefreshWindowsEvidenceAsync(work, thorough, Interlocked.Increment(ref windowsEvidenceGeneration));
        report += await CleanupRemovedWindowsCacheAsync(thorough);
        report += windows;
        if (!automatic)
        {
            cleanupCoordinator.ReportProgress(report);
            var previous = report;
            report += await ScanOrphanCacheDuplicatesAsync(scan => cleanupCoordinator.ReportProgress(previous + scan));
        }
        if (automatic && report.Skipped == 0 && report.Errors == 0)
            await TrimVerifiedOverflowAsync(nodes, snapshots, capturedGeneration);
        return report;
    }

    private async Task TrimVerifiedOverflowAsync(LinkedListNode<ClipboardDataPair>[] nodes,
        ClipboardCleanupSnapshot[] snapshots, long generation)
    {
        var removed = new List<ClipboardDataPair>();
        await RecordsLock.WaitAsync();
        try
        {
            if (!Settings.AutomaticCleanup || captureGeneration != generation) return;
            var captured = new Dictionary<ClipboardDataPair, ClipboardCleanupSnapshot>();
            for (var i = 0; i < nodes.Length; i++)
                if (nodes[i].List == RecordsList) captured[nodes[i].Value] = snapshots[i];
            if (captured.Count != RecordsList.Count || RecordsList.Any(pair =>
                !captured.TryGetValue(pair, out var snapshot) || !snapshot.Matches(pair.ClipboardData) ||
                pair.ClipboardData.FromWindowsClipboardHistory())) return;
            var node = RecordsList.Last;
            while (RecordsList.Count > Math.Max(0, Settings.MaxRecords) && node is not null)
            {
                var previous = node.Previous;
                if (!node.Value.ClipboardData.Pinned)
                {
                    removed.Add(node.Value);
                    RecordsList.Remove(node);
                }
                node = previous;
            }
            if (removed.Count > 0) ScoreHelper.Reset(RecordsList);
        }
        finally { RecordsLock.Release(); }
        foreach (var pair in removed)
        {
            try
            {
                if (pair.PreviewPanel.IsValueCreated) await pair.PreviewPanel.Value.Dispatcher.InvokeAsync(pair.Dispose);
                else pair.Dispose();
            }
            catch (Exception) { }
        }
    }
}
