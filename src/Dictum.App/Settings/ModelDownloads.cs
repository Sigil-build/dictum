using Dictum.Setup;

namespace Dictum.App.Settings;

/// <summary>
/// The app's one download queue, as Settings → Models and the wizard use it: commands, a snapshot, and a change
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

    void Unpause();

    void Cancel(string id);

    /// <summary>No queue: the self-test, and Settings opened before the shell started one.</summary>
    static IModelDownloads None { get; } = new NoModelDownloads();
}

/// <summary><see cref="IModelDownloads"/> over the shell's <see cref="ModelDownloads"/>; the worker's changes reach the UI thread through <c>post</c>.</summary>
public sealed class QueueDownloads : IModelDownloads
{
    private readonly ModelDownloads _queue;

    public QueueDownloads(ModelDownloads queue, Action<Action> post)
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

    public void Unpause()
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

internal sealed class NoModelDownloads : IModelDownloads
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

    public void Unpause()
    {
    }

    public void Cancel(string id)
    {
    }
}
