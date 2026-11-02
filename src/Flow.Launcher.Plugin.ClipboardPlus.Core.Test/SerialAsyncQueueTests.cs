// SerialAsyncQueueTests.cs
using Xunit;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Test;

public class SerialAsyncQueueTests
{
    [Fact]
    public async Task EachCallerWaitsForItsOwnOperationAndOperationsRunInOrder()
    {
        var queue = new SerialAsyncQueue();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<int>();
        var first = queue.ExecuteAsync(async () =>
        {
            events.Add(1);
            firstStarted.SetResult();
            await releaseFirst.Task;
            events.Add(2);
        });
        await firstStarted.Task;
        var second = queue.ExecuteAsync(() => { events.Add(3); return Task.CompletedTask; });
        var barrier = queue.WaitForSubmittedAsync();
        Assert.False(second.IsCompleted);
        Assert.False(barrier.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second, barrier);
        Assert.Equal(new[] { 1, 2, 3 }, events);
    }

    [Fact]
    public async Task FailureIsReportedToTheRightCallerAndDoesNotDiscardLaterWork()
    {
        var queue = new SerialAsyncQueue();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.ExecuteAsync(async () => { await gate.Task; throw new InvalidOperationException("synthetic"); });
        var ran = false;
        var second = queue.ExecuteAsync(() => { ran = true; return Task.CompletedTask; });
        gate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await second;
        Assert.True(ran);
    }
}
