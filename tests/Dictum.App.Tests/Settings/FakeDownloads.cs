using Dictum.App.Settings;
using Dictum.Setup;

namespace Dictum.App.Tests.Settings;

/// <summary>The download queue as the Models page sees it: the test sets its items and reads the commands.</summary>
internal sealed class FakeDownloads : IModelDownloads
{
    private readonly List<QueuedDownload> _items = [];

    public event Action? Changed;

    public IReadOnlyList<QueuedDownload> Items => [.. _items];

    public bool IsPaused { get; private set; }

    public List<string> Calls { get; } = [];

    /// <summary>Thrown by every command, as from a queue that cannot write downloads.json.</summary>
    public Exception? Throws { get; set; }

    public void Download(CatalogEntry entry)
    {
        Call($"download {entry.Id}");
        Set(new QueuedDownload(entry, DownloadState.Queued, 0));
    }

    public void Pause()
    {
        Call("pause");
        IsPaused = true;
        Changed?.Invoke();
    }

    public void Resume()
    {
        Call("resume");
        IsPaused = false;
        Changed?.Invoke();
    }

    public void Cancel(string id)
    {
        Call($"cancel {id}");
        _items.RemoveAll(i => i.Entry.Id == id);
        Changed?.Invoke();
    }

    /// <summary>Adds or replaces that model's entry, as the worker's progress does.</summary>
    public void Set(QueuedDownload item)
    {
        var index = _items.FindIndex(i => i.Entry.Id == item.Entry.Id);
        if (index >= 0)
        {
            _items[index] = item;
        }
        else
        {
            _items.Add(item);
        }

        Changed?.Invoke();
    }

    private void Call(string call)
    {
        if (Throws is not null)
        {
            throw Throws;
        }

        Calls.Add(call);
    }
}
