using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.Host.Daemon;

/// <summary>
/// The level meter behind <c>monitor-start</c>: the microphone runs without keeping samples, and stops when its client
/// no longer renews it within <see cref="IpcProtocol.MonitorLifetime"/>, e.g. after the desktop app crashed.
/// </summary>
/// <param name="gate">The daemon's gate, which a dictation, a provider test and an engine swap also hold.</param>
/// <param name="background">Runs the expiry loop and reports its failure.</param>
/// <param name="stopping">Cancelled when the daemon stops.</param>
internal sealed class LevelMonitor(
    SemaphoreSlim gate,
    Func<IDictationEngine> engine,
    TimeProvider time,
    Action<Task> background,
    CancellationToken stopping)
{
    private static readonly TimeSpan Lifetime = IpcProtocol.MonitorLifetime;

    /// <summary>How often the expiry is checked, so a meter outlives its lifetime by at most this.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    private volatile bool _monitoring;
    private DateTimeOffset _until;

    /// <summary>Read without the gate by the capture thread, which publishes frames while it is true.</summary>
    public bool IsMonitoring => _monitoring;

    /// <summary>Starts the meter, or renews the one that runs.</summary>
    public async Task StartAsync(string? device, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = engine();
            if (current.Pipeline.State != PipelineState.Idle)
            {
                throw new InvalidOperationException("A dictation is running; the level meter can start when it has finished.");
            }

            _until = time.GetUtcNow() + Lifetime;
            if (_monitoring)
            {
                return;
            }

            await current.Audio.StartAsync(device ?? current.Config.General.AudioDevice, ct, keepSamples: false).ConfigureAwait(false);
            _monitoring = true;
        }
        finally
        {
            gate.Release();
        }

        background(ExpireAsync());
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Call with the gate held.</summary>
    public async Task StopCoreAsync()
    {
        if (!_monitoring)
        {
            return;
        }

        _monitoring = false;
        await engine().Audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ExpireAsync()
    {
        while (_monitoring)
        {
            await Task.Delay(CheckInterval, time, stopping).ConfigureAwait(false);
            if (_monitoring && time.GetUtcNow() >= _until)
            {
                await gate.WaitAsync(stopping).ConfigureAwait(false);
                try
                {
                    if (time.GetUtcNow() >= _until)
                    {
                        await StopCoreAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
    }
}
