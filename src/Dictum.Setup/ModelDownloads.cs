using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Core;

namespace Dictum.Setup;

public enum DownloadState
{
    Queued,
    Downloading,
    Paused,
    Done,
    Failed,
}

/// <param name="Bytes">Bytes on disk so far, a kept partial file included.</param>
public sealed record QueuedDownload(CatalogEntry Entry, DownloadState State, long Bytes, string? Error = null);

/// <summary>
/// Downloads catalog files one at a time in the background. Pausing keeps the partial file; pending ids are saved to
/// <see cref="AppPaths.DownloadsFile"/>, so a restarted app continues where it stopped.
/// </summary>
public sealed class ModelDownloads(FileDownloader downloader, AppPaths paths, Catalog catalog, Func<long> freeDiskBytes) : IAsyncDisposable
{
    private readonly Lock _gate = new();

    // Save runs on the caller's thread (Pause, Resume, ...) and on the worker; one writer at a time, and the snapshot is
    // taken inside, so the file always ends with the newest state.
    private readonly Lock _saveGate = new();
    private readonly List<QueuedDownload> _items = [];
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private CancellationTokenSource? _current;
    private Task _worker = Task.CompletedTask;

    // An explicit pause, saved and restored; closing the app is not a pause, so a restart continues the downloads.
    private bool _paused;
    private bool _disposing;

    /// <summary>Raised on a worker thread after every change of one item.</summary>
    public event Action<QueuedDownload>? Changed;

    public IReadOnlyList<QueuedDownload> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>Adds the entries that are neither installed nor queued, then starts unless the queue is paused.</summary>
    public void Enqueue(IEnumerable<CatalogEntry> entries)
    {
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                var index = _items.FindIndex(i => i.Entry.Id == entry.Id);
                if (ModelFiles.IsInstalled(paths, entry) || (index >= 0 && _items[index].State is not (DownloadState.Failed or DownloadState.Done)))
                {
                    continue;
                }

                var queued = new QueuedDownload(entry, _paused ? DownloadState.Paused : DownloadState.Queued, PartialBytes(entry));
                if (index >= 0)
                {
                    _items[index] = queued;
                }
                else
                {
                    _items.Add(queued);
                }
            }
        }

        Save();
        Start();
    }

    /// <summary>Stops the running download (its partial file stays) and holds the rest.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].State == DownloadState.Queued)
                {
                    _items[i] = _items[i] with { State = DownloadState.Paused };
                }
            }

            _current?.Cancel();
        }

        Save();
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].State == DownloadState.Paused)
                {
                    _items[i] = _items[i] with { State = DownloadState.Queued };
                }
            }
        }

        Save();
        Start();
    }

    /// <summary>Removes the entry and deletes its partial file.</summary>
    public void Cancel(string id)
    {
        CatalogEntry? idle = null;
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Entry.Id == id);
            if (index < 0)
            {
                return;
            }

            var item = _items[index];
            _items.RemoveAt(index);
            if (item.State == DownloadState.Downloading)
            {
                // The worker deletes the partial file once the download has let go of it.
                _cancelled.Add(id);
                _current?.Cancel();
            }
            else
            {
                idle = item.Entry;
            }
        }

        if (idle is not null)
        {
            DeletePartial(idle);
        }

        Save();
    }

    /// <summary>Continues the downloads an earlier run saved; a paused queue stays paused.</summary>
    public void Restore()
    {
        if (!File.Exists(paths.DownloadsFile))
        {
            return;
        }

        SavedDownloads? saved;
        try
        {
            saved = JsonSerializer.Deserialize(File.ReadAllText(paths.DownloadsFile), ModelDownloadsJsonContext.Default.SavedDownloads);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable file must not stop the app from starting; the downloads can be started again.
            return;
        }

        if (saved is null)
        {
            return;
        }

        lock (_gate)
        {
            _paused = saved.Paused;
        }

        Enqueue(saved.Ids.Select(catalog.Find).OfType<CatalogEntry>());
    }

    /// <summary>Completes when nothing is downloading; a paused queue counts as idle.</summary>
    public async Task WhenIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            Task worker;
            lock (_gate)
            {
                worker = _worker;
            }

            await worker.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(worker, _worker))
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            _disposing = true;
            _current?.Cancel();
            worker = _worker;
        }

        await worker.ConfigureAwait(false);
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_worker.IsCompleted && !_paused && !_disposing)
            {
                _worker = Task.Run(RunAsync);
            }
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            QueuedDownload next;
            CancellationTokenSource cts;
            lock (_gate)
            {
                var index = _items.FindIndex(i => i.State == DownloadState.Queued);
                if (_paused || _disposing || index < 0)
                {
                    return;
                }

                next = _items[index] = _items[index] with { State = DownloadState.Downloading, Error = null };
                _current = cts = new CancellationTokenSource();
            }

            Changed?.Invoke(next);
            await DownloadAsync(next.Entry, cts).ConfigureAwait(false);
        }
    }

    /// <summary>Downloads one entry and records how it ended; takes over <paramref name="cts"/> and disposes it.</summary>
    private async Task DownloadAsync(CatalogEntry entry, CancellationTokenSource cts)
    {
        try
        {
            var needed = SetupRunner.NeededFor(entry.Size) - PartialBytes(entry);
            var free = freeDiskBytes();
            if (free < needed)
            {
                throw new DownloadException($"Not enough disk space in {paths.ModelsDirectory}: {SetupRunner.Format(needed)} needed, {SetupRunner.Format(free)} free.");
            }

            var progress = new SyncProgress<DownloadProgress>(p => Update(entry.Id, i => i with { Bytes = p.Bytes }));
            await downloader.DownloadAsync(entry, paths.ResolveModel(entry.File), progress, cts.Token).ConfigureAwait(false);
            Update(entry.Id, i => i with { State = DownloadState.Done, Bytes = entry.Size });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Update(entry.Id, i => i with { State = _paused ? DownloadState.Paused : DownloadState.Queued, Bytes = PartialBytes(entry) });
        }
        catch (Exception ex) when (ex is DownloadException or IOException or HttpRequestException)
        {
            Update(entry.Id, i => i with { State = DownloadState.Failed, Error = ex.Message });
        }
        finally
        {
            bool cancelled;
            lock (_gate)
            {
                _current = null;
                cancelled = _cancelled.Remove(entry.Id);
            }

            cts.Dispose();
            if (cancelled)
            {
                DeletePartial(entry);
            }

            SaveFromWorker();
        }
    }

    /// <summary>A save that fails here must not stop the queue; the next change writes the file again.</summary>
    private void SaveFromWorker()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file only tells a restarted app what to continue; a stale one at worst lists a download that has
            // finished since, which Enqueue skips as installed, or failed, which is then tried again.
        }
    }

    private void Update(string id, Func<QueuedDownload, QueuedDownload> change)
    {
        QueuedDownload updated;
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Entry.Id == id);
            if (index < 0)
            {
                return;
            }

            updated = _items[index] = change(_items[index]);
        }

        Changed?.Invoke(updated);
    }

    private void Save()
    {
        lock (_saveGate)
        {
            SavedDownloads saved;
            lock (_gate)
            {
                saved = new SavedDownloads(_paused, [.. _items
                    .Where(i => i.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused)
                    .Select(i => i.Entry.Id)]);
            }

            Directory.CreateDirectory(paths.DataDirectory);
            var temp = paths.DownloadsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, ModelDownloadsJsonContext.Default.SavedDownloads));
            File.Move(temp, paths.DownloadsFile, overwrite: true);
        }
    }

    private string PartialPath(CatalogEntry entry) => FileDownloader.PartialPath(paths.ResolveModel(entry.File));

    private long PartialBytes(CatalogEntry entry)
    {
        var partial = PartialPath(entry);
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
    }

    private void DeletePartial(CatalogEntry entry)
    {
        try
        {
            File.Delete(PartialPath(entry));
        }
        catch (IOException)
        {
            // Still held by something else; the next download of this file resumes or replaces it.
        }
    }
}

internal sealed record SavedDownloads(bool Paused, IReadOnlyList<string> Ids);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SavedDownloads))]
internal sealed partial class ModelDownloadsJsonContext : JsonSerializerContext;
