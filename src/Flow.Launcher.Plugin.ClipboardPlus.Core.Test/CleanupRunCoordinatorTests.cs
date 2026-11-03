// CleanupRunCoordinatorTests.cs
using System.Collections.Concurrent;
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class CleanupRunCoordinatorTests
{
    [Fact]
    public async Task ManualProgressAndCompletionPreserveSkipsAndErrors()
    {
        var update = new CleanupReport(1, 2, 3, 4, 5);
        var updates = new List<CleanupReport>();
        CleanupRunCoordinator? coordinator = null;
        coordinator = new CleanupRunCoordinator(work =>
        {
            coordinator!.ReportProgress(update);
            return Task.FromResult(update);
        });
        var request = coordinator.RequestManual(new SynchronousProgress(updates.Add));
        Assert.Equal(update, await request.Completion);
        Assert.Equal(new[] { update }, updates);
        await coordinator.StopAsync();
    }

    private sealed class SynchronousProgress(Action<CleanupReport> report) : IProgress<CleanupReport>
    {
        public void Report(CleanupReport value) => report(value);
    }

    [Fact]
    public async Task EvidenceRefreshesCoalesceAndNeverOverlapManualWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidenceFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new ConcurrentQueue<CleanupWork>();
        var coordinator = new CleanupRunCoordinator(async work =>
        {
            runs.Enqueue(work);
            if (work == CleanupWork.ManualThorough) { entered.SetResult(); await release.Task; }
            if (work == CleanupWork.WindowsEvidence) evidenceFinished.SetResult();
            return default;
        });
        var manual = coordinator.RequestManual();
        await entered.Task;
        coordinator.RequestWindowsEvidence();
        coordinator.RequestWindowsEvidence();
        Assert.Equal(new[] { CleanupWork.ManualThorough }, runs);
        release.SetResult();
        await manual.Completion;
        await evidenceFinished.Task;
        Assert.Equal(new[] { CleanupWork.ManualThorough, CleanupWork.WindowsEvidence }, runs);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ManualAfterBestEffortRunsThoroughlyAndConcurrentManualRequestsShareCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new ConcurrentQueue<CleanupWork>();
        var coordinator = new CleanupRunCoordinator(async work =>
        {
            runs.Enqueue(work);
            if (work == CleanupWork.AutomaticBestEffort) { entered.SetResult(); await release.Task; }
            return new CleanupReport(1, 0, 0, 0, 0);
        });
        coordinator.SetAutomaticEnabled(true);
        coordinator.RequestAutomatic(false);
        await entered.Task;
        var first = coordinator.RequestManual();
        var second = coordinator.RequestManual();
        Assert.False(first.AlreadyRunning);
        Assert.True(second.AlreadyRunning);
        Assert.Same(first.Completion, second.Completion);
        release.SetResult();
        Assert.Equal(1, (await first.Completion).RecordsRemoved);
        Assert.Equal(new[] { CleanupWork.AutomaticBestEffort, CleanupWork.ManualThorough }, runs);
    }

    [Fact]
    public async Task ManualAfterSuccessfulAutomaticThoroughRunsOnlyMissingManualWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new ConcurrentQueue<CleanupWork>();
        var coordinator = new CleanupRunCoordinator(async work =>
        {
            runs.Enqueue(work);
            if (work == CleanupWork.AutomaticThorough) { entered.SetResult(); await release.Task; return new CleanupReport(2, 0, 1, 0, 0); }
            return new CleanupReport(0, 3, 0, 1, 0);
        });
        coordinator.SetAutomaticEnabled(true);
        coordinator.RequestAutomatic(true);
        await entered.Task;
        var manual = coordinator.RequestManual();
        release.SetResult();
        Assert.Equal(new CleanupReport(2, 3, 1, 1, 0), await manual.Completion);
        Assert.Equal(new[] { CleanupWork.AutomaticThorough, CleanupWork.ManualExtrasOnly }, runs);
    }

    [Fact]
    public async Task FailedAutomaticThoroughIsRetriedByManualAndFailuresAreSanitized()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new ConcurrentQueue<CleanupWork>();
        var coordinator = new CleanupRunCoordinator(async work =>
        {
            runs.Enqueue(work);
            if (work == CleanupWork.AutomaticThorough) { entered.SetResult(); await release.Task; throw new InvalidOperationException("private synthetic data"); }
            return default;
        });
        coordinator.SetAutomaticEnabled(true);
        coordinator.RequestAutomatic(true);
        await entered.Task;
        var manual = coordinator.RequestManual();
        release.SetResult();
        Assert.Equal(default, await manual.Completion);
        Assert.Equal(new[] { CleanupWork.AutomaticThorough, CleanupWork.ManualThorough }, runs);
    }

    [Fact]
    public async Task ShutdownWaitsForAlreadyRequestedManualWorkAndRejectsNewWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new CleanupRunCoordinator(async work =>
        {
            entered.SetResult();
            await release.Task;
            return new CleanupReport(1, 0, 0, 0, 0);
        });
        var manual = coordinator.RequestManual();
        await entered.Task;
        var stop = coordinator.StopAsync();
        Assert.False(stop.IsCompleted);
        release.SetResult();
        Assert.Equal(1, (await manual.Completion).RecordsRemoved);
        await stop;
        Assert.Equal(1, (await coordinator.RequestManual().Completion).Errors);
    }

    [Fact]
    public async Task DisablingAutomaticDropsPendingWorkButManualRemainsAvailable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new ConcurrentQueue<CleanupWork>();
        var coordinator = new CleanupRunCoordinator(async work =>
        {
            runs.Enqueue(work);
            if (work == CleanupWork.AutomaticBestEffort) { entered.SetResult(); await release.Task; }
            return default;
        });
        coordinator.SetAutomaticEnabled(true);
        coordinator.RequestAutomatic(false);
        await entered.Task;
        coordinator.RequestAutomatic(false);
        coordinator.SetAutomaticEnabled(false);
        var manual = coordinator.RequestManual();
        release.SetResult();
        await manual.Completion;
        Assert.Equal(new[] { CleanupWork.AutomaticBestEffort, CleanupWork.ManualThorough }, runs);
    }
}
