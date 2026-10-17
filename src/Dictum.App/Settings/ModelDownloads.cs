using Dictum.Setup;

namespace Dictum.App.Settings;

/// <summary>
/// The app's one download queue (spec 9), as Settings → Models and the wizard use it: commands, a snapshot, and a change
/// event on the UI thread.
/// </summary>
public interface IModelDownloads
{
    /// <summary>Raised on the UI thread after the queue or one of its files changed.</summary>
    event Action? Changed;

    IReadOnlyList<QueuedDownload> Items { get; }

    bool IsPaused { get; }

    /// <summary>Queues the file. A paused queue resumes, since the click asks for the download now.</summary>
    void Download(CatalogEntry entry);

    void Pause();

    void Resume();

    void Cancel(string id);
}

/// <summary><see cref="IModelDownloads"/> over the shell's <see cref="DownloadQueue"/>; the worker's changes reach the UI thread through <c>post</c>.</summary>
public sealed class QueueDownloads : IModelDownloads
{
    private readonly DownloadQueue _queue;

    public QueueDownloads(DownloadQueue queue, Action<Action> post)
    {
        _queue = queue;
        queue.Changed += _ => post(() => Changed?.Invoke());
    }

    public event Action? Changed;

    public IReadOnlyList<QueuedDownload> Items => _queue.Items;

    public bool IsPaused => _queue.IsPaused;

    public void Download(CatalogEntry entry)
    {
        _queue.Enqueue([entry]);
        if (_queue.IsPaused)
        {
            _queue.Resume();
        }

        // A file that waits raises nothing on the worker yet; the page shows it now.
        Changed?.Invoke();
    }

    public void Pause()
    {
        _queue.Pause();
        Changed?.Invoke();
    }

    public void Resume()
    {
        _queue.Resume();
        Changed?.Invoke();
    }

    public void Cancel(string id)
    {
        _queue.Cancel(id);
        Changed?.Invoke();
    }
}

/// <summary>No queue: the self-test, and Settings opened before the shell started one.</summary>
public static class ModelDownloads
{
    public static IModelDownloads None { get; } = new NoQueue();

    private sealed class NoQueue : IModelDownloads
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<QueuedDownload> Items => [];

        public bool IsPaused => false;

        public void Download(CatalogEntry entry)
        {
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Cancel(string id)
        {
        }
    }
}
