// SerialAsyncQueue.cs
using System;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

/// <summary>Runs asynchronous operations in submission order, with a completion task for each operation.</summary>
internal sealed class SerialAsyncQueue
{
    private readonly object sync = new();
    private Task tail = Task.CompletedTask;

    public async Task ExecuteAsync(Func<Task> operation)
    {
        Task previous;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            previous = tail;
            tail = completion.Task;
        }

        await previous.ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            // A failed operation must not prevent later operations from running.
            completion.SetResult();
        }
    }

    public Task WaitForSubmittedAsync()
    {
        lock (sync)
        {
            return tail;
        }
    }
}
