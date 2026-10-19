namespace Dictum.Host.Daemon;

/// <summary>Work the daemon starts without awaiting it; disposal waits for it before it frees what that work uses.</summary>
internal sealed class BackgroundWork
{
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _running = [];

    public void Add(Task task)
    {
        lock (_gate)
        {
            _running.Add(task);
        }

        _ = task.ContinueWith(Remove, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Completes once everything added has ended, including work added while it waits.</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                running = [.. _running.Where(t => !t.IsCompleted)];
            }

            if (running.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Only the end matters here: each failure is reported, or deliberately ignored, where the work started.
            }
        }
    }

    private void Remove(Task task)
    {
        lock (_gate)
        {
            _running.Remove(task);
        }
    }
}
