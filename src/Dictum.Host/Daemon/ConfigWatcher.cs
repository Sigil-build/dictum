using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Host.Daemon;

/// <summary>
/// Reloads config.json when it changes on disk. An invalid file is reported and ignored, so the
/// daemon keeps running with the last good config.
/// </summary>
public sealed class ConfigWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly ConfigStore _store;
    private readonly Func<DictumConfig, IReadOnlyList<ConfigIssue>, Task> _onChanged;
    private readonly Func<ConfigException, Task> _onInvalid;
    private readonly ILog _log;
    private readonly FileSystemWatcher _watcher;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;

    public ConfigWatcher(ConfigStore store, Func<DictumConfig, IReadOnlyList<ConfigIssue>, Task> onChanged, Func<ConfigException, Task> onInvalid, ILog log)
    {
        _store = store;
        _onChanged = onChanged;
        _onInvalid = onInvalid;
        _log = log;
        var directory = Path.GetDirectoryName(Path.GetFullPath(store.Path))!;
        Directory.CreateDirectory(directory);

        // Editors often save by writing a temp file and renaming it, so watch renames too.
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(store.Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => Schedule();
        _watcher.Created += (_, _) => Schedule();
        _watcher.Renamed += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
        }
    }

    private void Schedule()
    {
        CancellationToken token;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }

        _ = ReloadAfterDelayAsync(token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct).ConfigureAwait(false);
            var (config, warnings) = _store.LoadWithIssues();
            foreach (var warning in warnings)
            {
                _log.Warn($"config: {warning}");
            }

            await _onChanged(config, warnings).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer change restarted the debounce, or the watcher was disposed.
        }
        catch (ConfigException ex)
        {
            _log.Warn($"Ignoring invalid config change: {ex.Message} {string.Join("; ", ex.Issues)}");
            await _onInvalid(ex).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // The editor may still hold the file; the next change event retries.
            _log.Debug($"Config not readable yet: {ex.Message}");
        }
    }
}
