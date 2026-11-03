// ClipboardPlus.Cleanup.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.ClipboardPlus;

public partial class ClipboardPlus
{
    private readonly CleanupRunCoordinator cleanupCoordinator;
    private bool lastAutomaticCleanup;
    private bool lastAutomaticCleanupThorough;

    public ManualCleanupRequest RequestManualCleanup(IProgress<CleanupReport>? progress = null) => cleanupCoordinator.RequestManual(progress);

    private sealed record ImageRelationship(LinkedListNode<ClipboardDataPair> Target, ClipboardCleanupSnapshot TargetSnapshot,
        LinkedListNode<ClipboardDataPair> Other, ClipboardCleanupSnapshot OtherSnapshot, string Reason);
    private List<ImageRelationship> imageRelationships = [];
    private bool imageRelationshipAutomatic;
    private bool imageRelationshipThorough;

    private async Task UpdateImageRelationshipsAsync(bool thorough)
    {
        // Best-effort passes do not verify images; retain still-valid proofs
        // from an earlier thorough pass instead of replacing them with guesses.
        if (!thorough) return;
        LinkedListNode<ClipboardDataPair>[] nodes;
        ClipboardCleanupSnapshot[] snapshots;
        bool automaticAtStart;
        bool thoroughAtStart;
        await RecordsLock.WaitAsync();
        try
        {
            automaticAtStart = Settings.AutomaticCleanup;
            thoroughAtStart = Settings.AutomaticCleanupThorough;
            var items = new List<LinkedListNode<ClipboardDataPair>>();
            for (var node = RecordsList.First; node is not null; node = node.Next)
                if (node.Value.ClipboardData.DataType == DataType.Image) items.Add(node);
            nodes = items.ToArray();
            snapshots = nodes.Select(node => new ClipboardCleanupSnapshot(node.Value.ClipboardData)).ToArray();
        }
        finally { RecordsLock.Release(); }
        var results = new List<ImageRelationship>();
        if (thorough)
        {
            var groups = snapshots.Select((snapshot, index) => (Index: index,
                    Fingerprint: ExactImagePixelComparer.Fingerprint(snapshot.Original.Data as BitmapSource)))
                .Where(item => item.Fingerprint is not null).GroupBy(item => item.Fingerprint);
            foreach (var group in groups)
            {
                var indexes = group.Select(item => item.Index).ToArray();
                // Partition by relevant metadata so large groups of identical,
                // unpinned images do not trigger quadratic pixel comparisons.
                var categories = indexes.GroupBy(index =>
                {
                    var data = snapshots[index].Original;
                    return (data.DataMd5, data.Pinned, data.Saved, Windows: data.FromWindowsClipboardHistory());
                }).Select(category => category.ToArray()).ToArray();
                foreach (var targetIndex in indexes)
                {
                    var left = snapshots[targetIndex].Original;
                    var found = 0;
                    foreach (var category in categories)
                    {
                        var otherIndex = category.FirstOrDefault(index => index != targetIndex, -1);
                        if (otherIndex < 0) continue;
                        var right = snapshots[otherIndex].Original;
                        var windowsUntrusted = !WindowsClipboardHelper.IsPinStateTrusted &&
                            (left.FromWindowsClipboardHistory() || right.FromWindowsClipboardHistory());
                        if (left.DataMd5 == right.DataMd5 && !left.Pinned && !right.Pinned && left.Saved == right.Saved && !windowsUntrusted) continue;
                        if (ExactImagePixelComparer.Compare(left.Data as BitmapSource, right.Data as BitmapSource) != ClipboardContentComparison.Equal) continue;
                        var representation = ClipboardContentComparer.Compare(left, right, true);
                        string? reason = representation == ClipboardContentComparison.Different ? "representation" :
                            representation == ClipboardContentComparison.Equal && (left.Pinned || right.Pinned) ? "pin" :
                            representation == ClipboardContentComparison.Equal && windowsUntrusted ? "windows" :
                            representation == ClipboardContentComparison.Equal && left.Saved != right.Saved &&
                                !left.FromWindowsClipboardHistory() && !right.FromWindowsClipboardHistory() ? "persistence" : null;
                        if (reason is null) continue;
                        results.Add(new(nodes[targetIndex], snapshots[targetIndex], nodes[otherIndex], snapshots[otherIndex], reason));
                        if (++found == 2) break; // The footer only needs one example and an additional-match indicator.
                        // Two verified peers from the same category are enough to indicate more matches.
                        var anotherIndex = category.FirstOrDefault(index => index != targetIndex && index != otherIndex, -1);
                        if (anotherIndex < 0) continue;
                        var another = snapshots[anotherIndex].Original;
                        if (ExactImagePixelComparer.Compare(left.Data as BitmapSource, another.Data as BitmapSource) != ClipboardContentComparison.Equal ||
                            ClipboardContentComparer.Compare(left, another, true) != representation) continue;
                        results.Add(new(nodes[targetIndex], snapshots[targetIndex], nodes[anotherIndex], snapshots[anotherIndex], reason));
                        if (++found == 2) break;
                    }
                }
            }
        }
        await RecordsLock.WaitAsync();
        try
        {
            if (automaticAtStart != Settings.AutomaticCleanup || thoroughAtStart != Settings.AutomaticCleanupThorough) return;
            imageRelationships = results.Where(item => item.Target.List == RecordsList && item.Other.List == RecordsList &&
                item.TargetSnapshot.Matches(item.Target.Value.ClipboardData) && item.OtherSnapshot.Matches(item.Other.Value.ClipboardData)).ToList();
            imageRelationshipAutomatic = Settings.AutomaticCleanup;
            imageRelationshipThorough = Settings.AutomaticCleanupThorough;
        }
        finally { RecordsLock.Release(); }
    }

    private async Task RefreshLoadedImagePreviewsAsync()
    {
        List<PreviewPanel> previews;
        await RecordsLock.WaitAsync();
        try
        {
            previews = RecordsList.Where(pair => pair.ClipboardData.DataType == DataType.Image && pair.PreviewPanel.IsValueCreated)
                .Select(pair => pair.PreviewPanel.Value).OfType<PreviewPanel>().ToList();
        }
        finally { RecordsLock.Release(); }
        foreach (var preview in previews)
        {
            if (!preview.Dispatcher.HasShutdownStarted)
                _ = preview.Dispatcher.InvokeAsync(() => _ = preview.ViewModel.RefreshDeduplicationExplanationAsync());
        }
    }

    public Task<string> GetImageDeduplicationExplanationAsync(ClipboardData data) =>
        GetImageDeduplicationExplanationAsync(data, null);

    public Task<string> GetImageDeduplicationExplanationAsync(ClipboardDataPair pair) =>
        GetImageDeduplicationExplanationAsync(null, pair);

    private async Task<string> GetImageDeduplicationExplanationAsync(ClipboardData? data, ClipboardDataPair? pair)
    {
        await RecordsLock.WaitAsync();
        try
        {
            if (Settings.AutomaticCleanup != imageRelationshipAutomatic || Settings.AutomaticCleanupThorough != imageRelationshipThorough) return string.Empty;
            var matches = imageRelationships.Where(item => (item.Reason != "windows" || !WindowsClipboardHelper.IsPinStateTrusted) &&
                item.Target.List == RecordsList && item.Other.List == RecordsList &&
                (pair is not null ? ReferenceEquals(item.Target.Value, pair) :
                    data.HasValue && item.Target.Value.ClipboardData.HashId == data.Value.HashId && item.TargetSnapshot.Matches(data.Value)) &&
                item.TargetSnapshot.Matches(item.Target.Value.ClipboardData) && item.OtherSnapshot.Matches(item.Other.Value.ClipboardData)).ToArray();
            if (matches.Length == 0) return string.Empty;
            var first = matches[0];
            var reason = first.Reason switch
            {
                "pin" => Localize.flowlauncher_plugin_clipboardplus_dedup_reason_pin(),
                "windows" => Localize.flowlauncher_plugin_clipboardplus_dedup_reason_windows(),
                "persistence" => Localize.flowlauncher_plugin_clipboardplus_dedup_reason_persistence(),
                _ => Localize.flowlauncher_plugin_clipboardplus_dedup_reason_representation()
            };
            var date = first.Other.Value.ClipboardData.CreateTime.ToString(CultureInfo);
            var result = Localize.flowlauncher_plugin_clipboardplus_dedup_relationship(date, reason);
            if (matches.Length > 1) result += " " + Localize.flowlauncher_plugin_clipboardplus_dedup_more();
            return result;
        }
        finally { RecordsLock.Release(); }
    }

    private long cleanupQueryGeneration;
    private sealed record CleanupResultView(List<Result> Results, Query Query, CancellationToken Token, long Generation);
    private CleanupResultView? currentResultView;

    private async Task PublishLiveResultsAsync(CleanupResultView view, bool rebuild = false)
    {
        try
        {
            if (view.Token.IsCancellationRequested || Interlocked.Read(ref cleanupQueryGeneration) != view.Generation) return;
            List<Result> live;
            await RecordsLock.WaitAsync(view.Token);
            try
            {
                if (rebuild && view.Query.FirstSearch != Settings.ClearKeyword)
                {
                    // Keep action objects (and their progress text), but recreate
                    // every record result so previously hidden records can reappear.
                    live = view.Results.Where(result => result.ContextData is not ClipboardDataPair).ToList();
                    var search = string.IsNullOrWhiteSpace(view.Query.Search) ? null : view.Query.Search;
                    foreach (var pair in RecordsList)
                    {
                        var result = GetResultFromClipboardData(pair, search);
                        if (result is not null) live.Add(result);
                    }
                    if (search is null) RankEmptyQueryRecords(live);
                }
                else
                {
                    var surviving = new HashSet<ClipboardDataPair>(RecordsList);
                    live = view.Results.Where(result => result.ContextData is not ClipboardDataPair pair ||
                        (surviving.Contains(pair) && !IsWindowsSuppressed(pair))).ToList();
                }
            }
            finally { RecordsLock.Release(); }
            if (!view.Token.IsCancellationRequested && Interlocked.Read(ref cleanupQueryGeneration) == view.Generation)
                ResultsUpdated?.Invoke(this, new ResultUpdatedEventArgs { Results = live, Query = view.Query });
        }
        catch (OperationCanceledException) { }
    }

    private Task PublishCurrentResultsAsync()
    {
        var view = Volatile.Read(ref currentResultView);
        return view is null ? Task.CompletedTask : PublishLiveResultsAsync(view, true);
    }

    private Result CreateCleanupAction(List<Result> results, Query query, CancellationToken token, long generation)
    {
        Result action = null!;
        void Publish() => _ = PublishLiveResultsAsync(new CleanupResultView(results, query, token, generation), true);
        var finished = 0;
        string FormatReport(CleanupReport report)
        {
            var text = Localize.flowlauncher_plugin_clipboardplus_cleanup_counts(report.RecordsRemoved, report.CacheFilesRemoved, report.WindowsItemsHidden);
            if (report.Skipped > 0) text += " " + Localize.flowlauncher_plugin_clipboardplus_cleanup_skipped(report.Skipped);
            if (report.Errors > 0) text += " " + Localize.flowlauncher_plugin_clipboardplus_cleanup_errors(report.Errors);
            return text;
        }
        async Task CompleteAsync(Task<CleanupReport> task)
        {
            var report = await task;
            Interlocked.Exchange(ref finished, 1);
            action.SubTitle = FormatReport(report);
            Publish();
        }
        action = new Result
        {
            Title = Localize.flowlauncher_plugin_clipboardplus_cleanup_action(),
            SubTitle = Localize.flowlauncher_plugin_clipboardplus_cleanup_action_subtitle(),
            IcoPath = PathHelper.ClearIconPath,
            Glyph = ResourceHelper.ClearGlyph,
            Score = 0,
            AddSelectedCount = false,
            Action = context =>
            {
                Interlocked.Exchange(ref finished, 0);
                var progress = new Progress<CleanupReport>(report =>
                {
                    if (Volatile.Read(ref finished) != 0) return;
                    action.SubTitle = Localize.flowlauncher_plugin_clipboardplus_cleanup_running() + " " + FormatReport(report);
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
        _ = RefreshLoadedImagePreviewsAsync();
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
        {
            var evidence = await RefreshWindowsEvidenceAsync(null, true, Interlocked.Read(ref windowsEvidenceGeneration));
            await PublishCurrentResultsAsync();
            return evidence;
        }
        if (work == CleanupWork.ManualExtrasOnly)
        {
            var windowsOnly = await RefreshWindowsEvidenceAsync(work, true, Interlocked.Increment(ref windowsEvidenceGeneration));
            windowsOnly += await CleanupRemovedWindowsCacheAsync(true);
            cleanupCoordinator.ReportProgress(windowsOnly);
            var extrasReport = windowsOnly + await ScanOrphanCacheDuplicatesAsync(scan => cleanupCoordinator.ReportProgress(windowsOnly + scan));
            await PublishCurrentResultsAsync();
            return extrasReport;
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
            var loadedIds = new HashSet<string>(snapshots.Select(snapshot => snapshot.Original.HashId), StringComparer.Ordinal);
            var stored = await Database.GetCleanupRecordsAsync(thorough, loadedIds);
            databaseSkipped = stored.Skipped;
            foreach (var data in stored.Records)
            {
                if (loadedIds.Contains(data.HashId)) continue;
                allNodes.Add(null);
                allSnapshots.Add(new ClipboardCleanupSnapshot(data));
            }
        }
        var failedLoadedCandidates = new HashSet<int>();
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
                    // If the retained saved record was outside the loaded
                    // window, replace the visible duplicate with that keeper.
                    // Otherwise consolidation would leave no visible copy.
                    if (keeperNode is null)
                    {
                        var visibleKeeper = keeper with { InitScore = duplicate.InitScore };
                        var pair = CreateClipboardDataPair(visibleKeeper);
                        keeperNode = RecordsList.AddBefore(duplicateNode, pair);
                        keeperSnapshot = new ClipboardCleanupSnapshot(visibleKeeper);
                        allNodes[keeperIndex] = keeperNode;
                        allSnapshots[keeperIndex] = keeperSnapshot;
                    }
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
        }, automatic ? null : cleanupCoordinator.ReportProgress, (_, duplicateIndex, outcome) =>
        {
            if (outcome != DuplicateRemovalResult.Removed && duplicateIndex < nodes.Length)
                failedLoadedCandidates.Add(duplicateIndex);
        });

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
        if (automatic)
        {
            var reserved = HistoryOverflowPolicy.CountUncertain(snapshots, thorough, failedLoadedCandidates);
            await TrimVerifiedOverflowAsync(nodes, snapshots, capturedGeneration, reserved);
        }
        await UpdateImageRelationshipsAsync(thorough);
        await RefreshLoadedImagePreviewsAsync();
        if (report.RecordsRemoved > 0 || report.WindowsItemsHidden > 0)
            await PublishCurrentResultsAsync();
        return report;
    }

    // Called under RecordsLock. A hard bound prevents captures outpacing the
    // worker indefinitely; pinned records remain protected even at the bound.
    private bool EnforceCaptureOverflowLimit(bool notify = true)
    {
        var removed = new List<ClipboardDataPair>();
        var limit = HistoryOverflowPolicy.Limit(Settings.MaxRecords, HistoryOverflowPolicy.MaximumOverflow);
        var unpinnedCount = RecordsList.Count(pair => !pair.ClipboardData.Pinned);
        foreach (var node in OldestUnpinnedNodes().Take(Math.Max(0, unpinnedCount - limit)))
        {
            removed.Add(node.Value);
            RecordsList.Remove(node);
        }
        if (removed.Count == 0) return false;
        foreach (var pair in removed) _ = DisposeDetachedRecordAsync(pair);
        if (notify) NotifyCaptureOverflowTrimmed();
        return true;
    }

    private void NotifyCaptureOverflowTrimmed()
    {
        ScoreHelper.Reset(RecordsList);
        _ = PublishCurrentResultsAsync();
    }

    private static async Task DisposeDetachedRecordAsync(ClipboardDataPair pair)
    {
        try
        {
            if (pair.PreviewPanel.IsValueCreated) await pair.PreviewPanel.Value.Dispatcher.InvokeAsync(pair.Dispose);
            else pair.Dispose();
        }
        catch (Exception) { }
    }

    private async Task TrimVerifiedOverflowAsync(LinkedListNode<ClipboardDataPair>[] nodes,
        ClipboardCleanupSnapshot[] snapshots, long generation, int reserved)
    {
        var removed = new List<ClipboardDataPair>();
        await RecordsLock.WaitAsync();
        try
        {
            if (!Settings.AutomaticCleanup || captureGeneration != generation) return;
            var captured = new Dictionary<ClipboardDataPair, ClipboardCleanupSnapshot>();
            for (var i = 0; i < nodes.Length; i++)
                if (nodes[i].List == RecordsList) captured[nodes[i].Value] = snapshots[i];
            // Changed or newly materialized records reserve their own slots;
            // they do not block unrelated capacity management indefinitely.
            var stale = RecordsList.Count(pair => !pair.ClipboardData.Pinned &&
                (!captured.TryGetValue(pair, out var snapshot) || !snapshot.Matches(pair.ClipboardData)));
            var limit = HistoryOverflowPolicy.Limit(Settings.MaxRecords, reserved + stale);
            var unpinnedCount = RecordsList.Count(pair => !pair.ClipboardData.Pinned);
            foreach (var node in OldestUnpinnedNodes().Take(Math.Max(0, unpinnedCount - limit)))
            {
                removed.Add(node.Value);
                RecordsList.Remove(node);
            }
            if (removed.Count > 0) ScoreHelper.Reset(RecordsList);
        }
        finally { RecordsLock.Release(); }
        if (removed.Count > 0) await PublishCurrentResultsAsync();
        foreach (var pair in removed) await DisposeDetachedRecordAsync(pair);
    }
}
