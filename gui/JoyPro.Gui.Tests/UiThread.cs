using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Gui.Tests;

// Runs a test body the way JoyPro runs its view model: on one thread with a synchronization context, so every
// continuation of an await (also of the fire-and-forget refreshes a page change starts) comes back to that thread,
// as Avalonia's UI thread does in the app. Without it (xUnit has no such context) those continuations run on pool
// threads at the same time as the test and change the view model's collections under its feet.
internal static class UiThread
{
    public static Task RunAsync(Func<Task> body)
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            SynchronizationContext? before = SynchronizationContext.Current;
            using SingleThreadContext context = new();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                Task task;
                try
                {
                    task = body();
                }
                catch (Exception ex)
                {
                    task = Task.FromException(ex);
                }
                task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
                context.RunUntilComplete();
                // Work still queued when the test ended is dropped, as when the window closes.
                if (context.Unhandled != null)
                    done.SetException(new InvalidOperationException("Work on the UI thread threw: " + context.Unhandled.Message, context.Unhandled));
                else if (task.IsCanceled)
                    done.SetCanceled();
                else if (task.Exception != null)
                    done.SetException(task.Exception.InnerExceptions);
                else
                    done.SetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(before);
            }
        })
        {
            IsBackground = true,
            Name = "Test UI thread"
        };
        thread.Start();
        return done.Task;
    }

    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();

        // An exception thrown by queued work (an async void handler): the app's UI thread records it; the test fails.
        public Exception? Unhandled { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                queue.TryAdd((d, state));
            }
            catch (InvalidOperationException)
            {
                // The test is over (adding completed or the queue disposed).
            }
        }

        // Nothing in JoyPro sends to the UI thread; run it where it is asked.
        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public override SynchronizationContext CreateCopy() => this;

        public void Complete()
        {
            try
            {
                queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void RunUntilComplete()
        {
            foreach ((SendOrPostCallback callback, object? state) in queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    Unhandled ??= ex;
                }
            }
        }

        public void Dispose() => queue.Dispose();
    }
}
