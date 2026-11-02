// ClipboardPlus.CacheCleanup.cs
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.ClipboardPlus;

public partial class ClipboardPlus
{
    private readonly object cacheOwnershipLock = new();
    private readonly ConcurrentQueue<string> removedWindowsCachePaths = new();

    private void RememberOwnedCacheFile(OwnedCacheFile ownership)
    {
        lock (cacheOwnershipLock)
            Settings.OwnedCacheFiles = (Settings.OwnedCacheFiles ?? []).Where(item => item.FileName != ownership.FileName)
                .Append(ownership).ToList();
        SaveCacheProvenance();
    }

    private void SaveCacheProvenance()
    {
        // Losing this best-effort save only makes a file ineligible for cleanup.
        _ = Task.Run(() =>
        {
            try { Context?.API.SaveSettingJsonStorage<Settings>(); }
            catch (Exception) { }
        });
    }

    private async Task<CleanupReport> DeleteRedundantCacheFileAsync(string candidatePath, string keeperPath,
        Func<bool> keeperIsStillSafe, bool automatic, bool windowsRemoval = false)
    {
        try
        {
            if (string.IsNullOrEmpty(candidatePath)) return default;
            var root = PathHelper.ImageCachePath;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(keeperPath)) return new CleanupReport(0, 0, 0, 1, 0);
            OwnedCacheFile[] names;
            lock (cacheOwnershipLock)
                names = (Settings.OwnedCacheFiles ?? []).Where(entry => entry is not null &&
                    string.Equals(entry.FileName, Path.GetFileName(candidatePath), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length != 1 || names[0].WindowsAssociated != windowsRemoval || !string.Equals(Path.GetFullPath(candidatePath),
                    Path.GetFullPath(Path.Combine(root, names[0].FileName)), StringComparison.OrdinalIgnoreCase))
                return new CleanupReport(0, 0, 0, 1, 0);
            using var proof = ManagedCacheFile.Prepare(root, names[0], keeperPath);
            if (proof is null) return new CleanupReport(0, 0, 0, 1, 0);
            var deletionError = false;
            var deleted = false;
            await RecordsLock.WaitAsync();
            try
            {
                if ((automatic && !Settings.AutomaticCleanup) || !keeperIsStillSafe() || HasUncertainOrMatchingCacheReference(candidatePath))
                    return new CleanupReport(0, 0, 0, 1, 0);
                deleted = await Database.TryDeleteUnreferencedCacheFileAsync(candidatePath,
                    () => proof.DeleteIf(() => keeperIsStillSafe() && !HasUncertainOrMatchingCacheReference(candidatePath), out deletionError));
            }
            finally { RecordsLock.Release(); }
            if (!deleted) return new CleanupReport(0, 0, 0, deletionError ? 0 : 1, deletionError ? 1 : 0);
            lock (cacheOwnershipLock)
                Settings.OwnedCacheFiles = (Settings.OwnedCacheFiles ?? []).Where(entry => entry != names[0]).ToList();
            SaveCacheProvenance();
            return new CleanupReport(0, 1, 0, 0, 0);
        }
        catch (Exception)
        {
            // No paths, file contents or exception messages enter diagnostics.
            return new CleanupReport(0, 0, 0, 0, 1);
        }
    }

    private async Task<CleanupReport> CleanupRemovedWindowsCacheAsync()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (removedWindowsCachePaths.TryDequeue(out var path)) paths.Add(path);
        var total = default(CleanupReport);
        foreach (var path in paths)
        {
            if (!Settings.AutomaticCleanup) return total;
            List<(LinkedListNode<ClipboardDataPair> Node, ClipboardCleanupSnapshot Snapshot)> keepers = [];
            await RecordsLock.WaitAsync();
            try
            {
                for (var node = RecordsList.First; node is not null; node = node.Next)
                    if (node.Value.ClipboardData.DataType == DataType.Image &&
                        !string.IsNullOrEmpty(node.Value.ClipboardData.CachedImagePath))
                        keepers.Add((node, new ClipboardCleanupSnapshot(node.Value.ClipboardData)));
            }
            finally { RecordsLock.Release(); }
            foreach (var keeper in keepers)
            {
                var report = await DeleteRedundantCacheFileAsync(path, keeper.Snapshot.Original.CachedImagePath,
                    () => keeper.Node.List == RecordsList && keeper.Snapshot.Matches(keeper.Node.Value.ClipboardData), true, true);
                if (report.CacheFilesRemoved > 0 || report.Errors > 0)
                {
                    total += report;
                    break;
                }
            }
        }
        return total;
    }

    private async Task<CleanupReport> ScanOrphanCacheDuplicatesAsync()
    {
        var root = PathHelper.ImageCachePath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return default;
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return new CleanupReport(0, 0, 0, 1, 0);
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await RecordsLock.WaitAsync();
            try
            {
                foreach (var pair in RecordsList)
                {
                    var path = pair.ClipboardData.CachedImagePath;
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!Path.IsPathFullyQualified(path)) return new CleanupReport(0, 0, 0, 1, 0);
                    references.Add(Path.GetFullPath(path));
                }
                foreach (var path in await Database.GetCacheReferencesAsync())
                {
                    if (!Path.IsPathFullyQualified(path)) return new CleanupReport(0, 0, 0, 1, 0);
                    references.Add(Path.GetFullPath(path));
                }
            }
            finally { RecordsLock.Release(); }

            var files = new List<(string Path, long Size, long LastWrite, string Hash, bool Referenced)>();
            var skipped = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.png", SearchOption.TopDirectoryOnly))
            {
                if (ManagedCacheFile.TryFingerprint(root, path, out var size, out var lastWrite, out var hash))
                    files.Add((path, size, lastWrite, hash, references.Contains(Path.GetFullPath(path))));
                else skipped++;
            }
            var report = new CleanupReport(0, 0, 0, skipped, 0);
            foreach (var group in files.GroupBy(file => (file.Size, file.Hash)))
            {
                var ordered = group.OrderByDescending(file => file.Referenced).ThenByDescending(file => file.LastWrite)
                    .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ThenBy(file => file.Path, StringComparer.Ordinal).ToArray();
                if (ordered.Length < 2) continue;
                var keeper = ordered[0].Path;
                foreach (var file in ordered.Skip(1))
                {
                    if (file.Referenced) continue;
                    report += await DeleteRedundantCacheFileAsync(file.Path, keeper, () => File.Exists(keeper), false);
                    await Task.Yield();
                }
            }
            return report;
        }
        catch (Exception)
        {
            return new CleanupReport(0, 0, 0, 0, 1);
        }
    }

    // Called with RecordsLock held. Any uncertain reference prevents deletion.
    private bool HasUncertainOrMatchingCacheReference(string candidatePath)
    {
        return !CacheReferenceSafety.IsUnreferenced(candidatePath,
            RecordsList.Select(pair => pair.ClipboardData.CachedImagePath));
    }
}
