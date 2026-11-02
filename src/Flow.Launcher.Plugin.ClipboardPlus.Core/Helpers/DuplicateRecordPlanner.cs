// DuplicateRecordPlanner.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>A proposed deletion, expressed as indexes into the supplied snapshot.</summary>
public readonly record struct DuplicateRecordCandidate(int KeeperIndex, int DuplicateIndex);

public sealed record DuplicatePlanningResult(IReadOnlyList<DuplicateRecordCandidate> Candidates, int Skipped);

/// <summary>
/// Plans possible plugin-record consolidation without mutating history. The plan
/// is advisory: callers must revalidate content, persistence and pin state while
/// coordinating with the history and database before committing any removal.
/// Windows-origin records are deliberately outside this planner's scope.
/// </summary>
public static class DuplicateRecordPlanner
{
    public static IReadOnlyList<DuplicateRecordCandidate> Plan(IReadOnlyList<ClipboardData> records, bool verifyImages) =>
        PlanDetailed(records, verifyImages).Candidates;

    public static DuplicatePlanningResult PlanDetailed(IReadOnlyList<ClipboardData> records, bool verifyImages)
    {
        var candidates = new List<DuplicateRecordCandidate>();
        var skipped = 0;
        var ambiguousIds = records.Where(record => !string.IsNullOrEmpty(record.HashId))
            .GroupBy(record => record.HashId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var groups = records.Select((record, index) => (Record: record, Index: index))
            .Where(item => !item.Record.Pinned && !item.Record.FromWindowsClipboardHistory() &&
                           !string.IsNullOrEmpty(item.Record.DataMd5) && !string.IsNullOrEmpty(item.Record.HashId) &&
                           !ambiguousIds.Contains(item.Record.HashId))
            .GroupBy(item => (item.Record.DataType, item.Record.DataMd5));

        foreach (var group in groups)
        {
            // Hash collisions form separate equality groups. A retained unpinned
            // record always remains even if matching pinned records exist.
            var keepers = new List<(ClipboardData Record, int Index)>();
            foreach (var item in group.OrderByDescending(item => item.Record.CreateTime).ThenBy(item => item.Index))
            {
                var matched = false;
                var verificationUnavailable = false;
                var differentPersistence = false;
                foreach (var keeper in keepers)
                {
                    var comparison = !verifyImages &&
                        (!DuplicateVerificationPolicy.CanVerifyBestEffort(keeper.Record) ||
                         !DuplicateVerificationPolicy.CanVerifyBestEffort(item.Record))
                        ? ClipboardContentComparison.Unavailable
                        : ClipboardContentComparer.Compare(keeper.Record, item.Record, verifyImages);
                    if (comparison != ClipboardContentComparison.Equal)
                    {
                        verificationUnavailable |= comparison == ClipboardContentComparison.Unavailable;
                        continue;
                    }
                    if (keeper.Record.Saved != item.Record.Saved)
                    {
                        differentPersistence = true;
                        continue;
                    }
                    matched = true;
                    candidates.Add(new DuplicateRecordCandidate(keeper.Index, item.Index));
                    break;
                }
                if (!matched)
                {
                    if (verificationUnavailable || differentPersistence) skipped++;
                    keepers.Add(item);
                }
            }
        }
        return new DuplicatePlanningResult(candidates, skipped);
    }
}
