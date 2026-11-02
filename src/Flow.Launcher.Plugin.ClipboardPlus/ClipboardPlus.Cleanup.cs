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
        if (work == CleanupWork.ManualExtrasOnly)
        {
            var windowsOnly = await RefreshWindowsEvidenceAsync(work, true, Interlocked.Increment(ref windowsEvidenceGeneration));
            return windowsOnly + await CleanupRemovedWindowsCacheAsync() + await ScanOrphanCacheDuplicatesAsync();
        }
        var automatic = work is CleanupWork.AutomaticBestEffort or CleanupWork.AutomaticThorough;
        var thorough = work is CleanupWork.AutomaticThorough or CleanupWork.ManualThorough;
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
        finally
        {
            RecordsLock.Release();
        }

        var cacheCandidates = new List<(string Path, string KeeperPath, LinkedListNode<ClipboardDataPair> Keeper, ClipboardCleanupSnapshot Snapshot)>();
        var report = await DuplicateCleanupEngine.RunAsync(snapshots, thorough, async (keeperIndex, duplicateIndex) =>
        {
            var keeperSnapshot = snapshots[keeperIndex];
            var duplicateSnapshot = snapshots[duplicateIndex];
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
                var keeperNode = nodes[keeperIndex];
                var duplicateNode = nodes[duplicateIndex];
                if ((automatic && !Settings.AutomaticCleanup) ||
                    keeperNode.List != RecordsList || duplicateNode.List != RecordsList ||
                    !keeperSnapshot.Matches(keeperNode.Value.ClipboardData) ||
                    !duplicateSnapshot.Matches(duplicateNode.Value.ClipboardData))
                {
                    return DuplicateRemovalResult.Skipped;
                }
                if (prepared is not null && !await Database.CommitPreparedDuplicateDeletionAsync(prepared))
                {
                    return DuplicateRemovalResult.Skipped;
                }
                removed = duplicateNode.Value;
                if (duplicate.DataType == DataType.Image && !string.IsNullOrEmpty(duplicate.CachedImagePath))
                    cacheCandidates.Add((duplicate.CachedImagePath, keeper.CachedImagePath, keeperNode, keeperSnapshot));
                RecordsList.Remove(duplicateNode);
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
                if (removed.PreviewPanel.IsValueCreated)
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
        report += await CleanupRemovedWindowsCacheAsync();
        if (!automatic) report += await ScanOrphanCacheDuplicatesAsync();
        return report + windows;
    }
}
