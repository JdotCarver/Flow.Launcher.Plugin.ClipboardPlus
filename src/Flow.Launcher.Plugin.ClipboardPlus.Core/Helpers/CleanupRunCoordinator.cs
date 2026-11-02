// CleanupRunCoordinator.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

public enum CleanupWork
{
    AutomaticBestEffort,
    AutomaticThorough,
    ManualThorough,
    ManualExtrasOnly,
    WindowsEvidence
}

/// <summary>Counts and status contain no clipboard content or file paths.</summary>
public readonly record struct CleanupReport(int RecordsRemoved, int CacheFilesRemoved, int WindowsItemsHidden, int Skipped, int Errors)
{
    public static CleanupReport operator +(CleanupReport a, CleanupReport b) => new(
        a.RecordsRemoved + b.RecordsRemoved, a.CacheFilesRemoved + b.CacheFilesRemoved,
        a.WindowsItemsHidden + b.WindowsItemsHidden, a.Skipped + b.Skipped, a.Errors + b.Errors);
}

public readonly record struct ManualCleanupRequest(bool AlreadyRunning, Task<CleanupReport> Completion);

/// <summary>
/// Serializes cleanup work and coalesces requests. The worker owns no UI or
/// clipboard data; it can continue after the requesting UI has closed.
/// </summary>
public sealed class CleanupRunCoordinator
{
    private readonly object sync = new();
    private readonly Func<CleanupWork, Task<CleanupReport>> execute;
    private bool automaticEnabled;
    private bool workerRunning;
    private bool stopping;
    private Task? worker;
    private CleanupWork? currentWork;
    private CleanupWork? pendingAutomatic;
    private bool pendingEvidence;
    private TaskCompletionSource<CleanupReport>? manualCompletion;
    private readonly List<IProgress<CleanupReport>> manualProgress = [];
    private bool manualPending;
    private bool manualCanReuseAutomatic;
    private CleanupReport reusableAutomaticReport;

    public CleanupRunCoordinator(Func<CleanupWork, Task<CleanupReport>> execute) =>
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public void SetAutomaticEnabled(bool enabled)
    {
        lock (sync)
        {
            automaticEnabled = enabled && !stopping;
            if (!enabled)
            {
                pendingAutomatic = null;
            }
        }
    }

    public void RequestAutomatic(bool thorough)
    {
        lock (sync)
        {
            if (!automaticEnabled || stopping)
            {
                return;
            }
            // A request during a run can represent a new copy not covered by
            // that run's snapshot. Keep one follow-up, promoting it if needed.
            var requested = thorough ? CleanupWork.AutomaticThorough : CleanupWork.AutomaticBestEffort;
            if (pendingAutomatic != CleanupWork.AutomaticThorough)
            {
                pendingAutomatic = requested;
            }
            StartWorkerIfNeeded();
        }
    }

    /// <summary>Coalesces manual-suppression evidence refreshes with cleanup work.</summary>
    public void RequestWindowsEvidence()
    {
        lock (sync)
        {
            if (stopping) return;
            pendingEvidence = true;
            StartWorkerIfNeeded();
        }
    }

    public ManualCleanupRequest RequestManual(IProgress<CleanupReport>? progress = null)
    {
        lock (sync)
        {
            if (progress is not null && !stopping) manualProgress.Add(progress);
            if (manualCompletion is not null)
            {
                return new ManualCleanupRequest(true, manualCompletion.Task);
            }
            if (stopping)
            {
                return new ManualCleanupRequest(false, Task.FromResult(new CleanupReport(0, 0, 0, 0, 1)));
            }
            manualCompletion = new TaskCompletionSource<CleanupReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            manualPending = true;
            manualCanReuseAutomatic = currentWork == CleanupWork.AutomaticThorough && pendingAutomatic is null;
            reusableAutomaticReport = default;
            var request = new ManualCleanupRequest(false, manualCompletion.Task);
            StartWorkerIfNeeded();
            return request;
        }
    }

    /// <summary>Reports non-sensitive interim counts only to active manual requesters.</summary>
    public void ReportProgress(CleanupReport report)
    {
        IProgress<CleanupReport>[] observers;
        lock (sync)
        {
            if (currentWork is not (CleanupWork.ManualThorough or CleanupWork.ManualExtrasOnly)) return;
            observers = manualProgress.ToArray();
            report += reusableAutomaticReport;
        }
        foreach (var observer in observers)
        {
            try { observer.Report(report); }
            catch (Exception) { }
        }
    }

    private void StartWorkerIfNeeded()
    {
        if (!workerRunning)
        {
            workerRunning = true;
            worker = Task.Run(RunAsync);
        }
    }

    /// <summary>Stops future automatic work and waits for an already-requested manual pass.</summary>
    public Task StopAsync()
    {
        lock (sync)
        {
            stopping = true;
            automaticEnabled = false;
            pendingAutomatic = null;
            pendingEvidence = false;
            return worker ?? Task.CompletedTask;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            CleanupWork work;
            CleanupReport prior = default;
            TaskCompletionSource<CleanupReport>? manual = null;
            lock (sync)
            {
                if (manualPending)
                {
                    manualPending = false;
                    manual = manualCompletion;
                    work = manualCanReuseAutomatic ? CleanupWork.ManualExtrasOnly : CleanupWork.ManualThorough;
                    prior = reusableAutomaticReport;
                    manualCanReuseAutomatic = false;
                }
                else if (automaticEnabled && pendingAutomatic is { } automatic)
                {
                    work = automatic;
                    pendingAutomatic = null;
                }
                else if (pendingEvidence)
                {
                    pendingEvidence = false;
                    work = CleanupWork.WindowsEvidence;
                }
                else
                {
                    currentWork = null;
                    workerRunning = false;
                    return;
                }
                currentWork = work;
            }

            CleanupReport report;
            try
            {
                report = await execute(work).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Automatic failures are silent, and manual failures are
                // conveyed without revealing exception messages or content.
                report = new CleanupReport(0, 0, 0, 0, 1);
            }

            lock (sync)
            {
                if (manual is not null)
                {
                    manualCompletion = null;
                    manualProgress.Clear();
                    manual.SetResult(prior + report);
                }
                else if (manualPending && manualCanReuseAutomatic)
                {
                    // Failure or newly queued automatic work means the manual
                    // pass must verify everything rather than reuse this run.
                    if (report.Errors != 0 || pendingAutomatic is not null)
                    {
                        manualCanReuseAutomatic = false;
                    }
                    else
                    {
                        reusableAutomaticReport = report;
                    }
                }
                currentWork = null;
            }
        }
    }
}
