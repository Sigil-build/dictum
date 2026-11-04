using Dictum.Core;
using Dictum.Core.Diagnostics;

namespace Dictum.Plugins;

/// <summary>What makes two launches the same process: the plugin, where it is, its version, and its model and threads.</summary>
public sealed record PluginKey(string PluginId, string Folder, string Version, string? Model, int? Threads)
{
    public static PluginKey Of(PluginLaunch launch) => new(launch.PluginId, launch.Folder, launch.Version, launch.Model, launch.Threads);
}

/// <summary>How plugins start, and how often one that stops by itself is started again.</summary>
/// <param name="MaxRestarts">Restarts allowed within <paramref name="RestartWindow"/>; past them the plugin stays down.</param>
/// <param name="RestartWindow">5 minutes when null, measured on the host's clock.</param>
/// <param name="RestartDelay">1 s when null: the wait between a plugin's unasked exit and its restart.</param>
public sealed record PluginHostOptions(PluginTimings Timings, int MaxRestarts = 3, TimeSpan? RestartWindow = null, TimeSpan? RestartDelay = null)
{
    public static PluginHostOptions Default { get; } = new(PluginTimings.Default);

    internal TimeSpan Window => RestartWindow ?? TimeSpan.FromMinutes(5);

    internal TimeSpan Delay => RestartDelay ?? TimeSpan.FromSeconds(1);
}

/// <summary>
/// The plugin processes of one Dictum process, one per <see cref="PluginKey"/>. A provider holds a lease on its
/// instance for as long as it exists, so a config reload whose new engine takes its lease before the old engine lets
/// go keeps the process, even one still loading its model. <see cref="StopUnusedAsync"/> then stops what no engine
/// holds. No program runs before the user confirmed it: the trust record is checked before every launch.
/// </summary>
/// <param name="groups">Where each plugin process starts, so that what it starts ends with it.</param>
public sealed class PluginHost(PluginTrustStore trust, IProcessGroups groups, ILog log, PluginHostOptions? options = null, TimeProvider? time = null)
    : IAsyncDisposable
{
    private readonly PluginHostOptions _options = options ?? PluginHostOptions.Default;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<PluginKey, PluginInstance> _instances = [];

    /// <summary>Every start runs under it, not under the token of whoever asked for it, and disposal cancels it.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>The sweeps still stopping what they removed, which disposal waits for.</summary>
    private readonly HashSet<Task> _sweeps = [];

    private bool _disposed;

    /// <summary>The client every request to a plugin goes through, its health polls included; it never uses a proxy.</summary>
    public HttpClient Http { get; } = PluginHttp.CreateClient();

    /// <summary>The groups every plugin process starts in, for a launch outside the host such as the probe of <c>plugins add</c>.</summary>
    public IProcessGroups Groups => groups;

    /// <summary>Every instance no sweep has removed yet, leased or not; for doctor and the tests.</summary>
    public IReadOnlyCollection<PluginInstance> Instances
    {
        get
        {
            lock (_gate)
            {
                return [.. _instances.Values];
            }
        }
    }

    /// <summary>
    /// A lease on the instance for this launch, which starts nothing until <see cref="PluginInstance.ReadyAsync"/>.
    /// The same key gets the same instance. A new lease on a failed instance makes it start afresh at the next
    /// <see cref="PluginInstance.ReadyAsync"/>: a reload is a retry.
    /// </summary>
    public PluginLease Acquire(PluginLaunch launch)
    {
        var key = PluginKey.Of(launch);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_instances.TryGetValue(key, out var instance))
            {
                instance = new PluginInstance(launch, trust, log, Http, groups, _options, _time, _lifetime.Token);
                _instances.Add(key, instance);
            }

            instance.AddLease();
            return new PluginLease(instance);
        }
    }

    /// <summary>Stops and forgets every instance that no lease holds, a start in progress included.</summary>
    /// <param name="isCurrent">
    /// Whether the engine that asks for the sweep is still the one in use; null sweeps regardless. A reload may replace
    /// and dispose that engine before its successor has taken its leases, which it takes at its own warm-up: a sweep
    /// for a replaced engine stops nothing, and the successor's sweep follows its warm-up. It is asked under the host's
    /// lock, so it must not wait or call the host.
    /// </param>
    public async Task StopUnusedAsync(Func<bool>? isCurrent = null)
    {
        List<PluginInstance> unused;
        var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            // Under the host's lock no lease can be taken meanwhile, so an instance removed here has none for good.
            unused = [.. _instances.Values.Where(instance => !instance.IsLeased)];

            // Asked once the leases are read, since letting go of a lease does not take this lock. A reload replaces the
            // engine before it disposes the old one, so an engine still current here had let go of nothing it uses.
            if (isCurrent is not null && !isCurrent())
            {
                return;
            }

            foreach (var instance in unused)
            {
                _instances.Remove(instance.Key);
            }

            _sweeps.Add(swept.Task);
        }

        try
        {
            foreach (var instance in unused)
            {
                log.Info($"Plugin {instance.Key.PluginId} is no longer used; stopping it");
            }

            await Task.WhenAll(unused.Select(instance => instance.StopAsync())).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _sweeps.Remove(swept.Task);
            }

            swept.SetResult();
        }
    }

    /// <summary>
    /// Cancels every start and stops every plugin, those a sweep is still stopping included. A part that fails does not
    /// stop the rest; the first failure is thrown once all of it has run.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        PluginInstance[] all;
        Task[] sweeps;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = [.. _instances.Values];
            _instances.Clear();
            sweeps = [.. _sweeps];
        }

        try
        {
            // A callback on the token that throws fails the cancel, after the others have run.
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Waits for every stop, whichever fails.
                await Task.WhenAll(all.Select(instance => instance.StopAsync()).Concat(sweeps)).ConfigureAwait(false);
            }
            finally
            {
                Http.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}

/// <summary>One provider's hold on a plugin instance. Disposing it only lets go; the next sweep stops what nobody holds.</summary>
public sealed class PluginLease : IDisposable
{
    private int _disposed;

    internal PluginLease(PluginInstance instance)
    {
        Instance = instance;
    }

    public PluginInstance Instance { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Instance.Release();
        }
    }
}
