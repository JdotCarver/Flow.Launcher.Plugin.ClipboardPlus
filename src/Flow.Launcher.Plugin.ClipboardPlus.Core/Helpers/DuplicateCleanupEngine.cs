// DuplicateCleanupEngine.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

public enum DuplicateRemovalResult
{
    Removed,
    Skipped
}

/// <summary>
/// Plans against detached snapshots and applies candidates individually through
/// a supplied owner. The owner must revalidate both live records, persistence,
/// and any database state atomically before returning Removed.
/// </summary>
public static class DuplicateCleanupEngine
{
    public static async Task<CleanupReport> RunAsync(
        IReadOnlyList<ClipboardCleanupSnapshot> snapshots,
        bool verifyImages,
        Func<int, int, Task<DuplicateRemovalResult>> apply)
    {
        var records = snapshots.Select(snapshot => snapshot.CreatePlanningRecord()).ToArray();
        var plan = DuplicateRecordPlanner.Plan(records, verifyImages);
        var removed = 0;
        var skipped = 0;
        var errors = 0;
        foreach (var candidate in plan)
        {
            try
            {
                var result = await apply(candidate.KeeperIndex, candidate.DuplicateIndex).ConfigureAwait(false);
                if (result == DuplicateRemovalResult.Removed)
                {
                    removed++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception)
            {
                // Other independent candidates may still be safely processed.
                // Do not expose the exception or any clipboard contents.
                errors++;
            }
        }
        return new CleanupReport(removed, 0, 0, skipped, errors);
    }
}
