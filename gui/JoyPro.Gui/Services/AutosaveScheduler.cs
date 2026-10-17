using System;
using System.Threading;
using System.Threading.Tasks;

namespace JoyPro.Gui.Services;

// Autosave (docs/profile-history.md): a change marks the profile dirty, the save runs once the changes stop for the
// delay (2 s), and FlushAsync saves at once (closing JoyPro, switching the profile, before an export or import).
// Saves never overlap: a change during a save is saved right after it. The save runs on the context Schedule was
// called on (the UI thread for the GUI).
public sealed class AutosaveScheduler
{
    private readonly Func<CancellationToken, Task> save;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private CancellationTokenSource? timer;
    private Task? running;
    private bool dirty;

    public AutosaveScheduler(Func<CancellationToken, Task> save, TimeSpan debounce, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.save = save;
        Debounce = debounce;
        this.delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    public TimeSpan Debounce { get; set; }

    // Changes that are not saved yet.
    public bool IsPending => dirty;

    public bool IsSaving => running is { IsCompleted: false };

    public int SaveCount { get; private set; }

    public Exception? LastError { get; private set; }

    public DateTime? LastSavedUtc { get; private set; }

    // Pending, saving, saved or failed changed.
    public event EventHandler? StateChanged;

    // A change: save once the changes stop for the debounce time.
    public void Schedule()
    {
        dirty = true;
        timer?.Cancel();
        CancellationTokenSource cts = new();
        timer = cts;
        _ = WaitThenSaveAsync(cts.Token);
        RaiseStateChanged();
    }

    // Forget the pending changes (the profile they belong to is gone).
    public void Cancel()
    {
        timer?.Cancel();
        timer = null;
        dirty = false;
        RaiseStateChanged();
    }

    // Save now if anything is pending, and wait for a save that runs.
    public async Task FlushAsync()
    {
        timer?.Cancel();
        timer = null;
        for (int guard = 0; guard < 10; guard++)
        {
            Task? current = running;
            if (current is { IsCompleted: false })
            {
                await AwaitQuietly(current);
                continue;
            }
            if (!dirty)
                return;
            await SaveNowAsync();
            if (LastError != null)
                return;
        }
    }

    private async Task WaitThenSaveAsync(CancellationToken token)
    {
        try
        {
            await delay(Debounce, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested)
            return;
        // A save already running: this change is saved right after it.
        Task? current = running;
        if (current is { IsCompleted: false })
            await AwaitQuietly(current);
        if (token.IsCancellationRequested || !dirty)
            return;
        await SaveNowAsync();
    }

    private async Task SaveNowAsync()
    {
        dirty = false;
        Task task = RunSaveAsync();
        running = task;
        RaiseStateChanged();
        await AwaitQuietly(task);
        RaiseStateChanged();
    }

    private async Task RunSaveAsync()
    {
        try
        {
            await save(CancellationToken.None);
            SaveCount++;
            LastError = null;
            LastSavedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // The changes stay pending: the next change or flush tries again.
            LastError = ex;
            dirty = true;
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // RunSaveAsync keeps the error.
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
