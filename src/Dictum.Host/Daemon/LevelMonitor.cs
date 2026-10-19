using Dictum.Core.Pipeline;

namespace Dictum.Host.Daemon;

/// <summary>
/// The level meter behind <c>monitor-start</c>: the microphone runs without keeping samples, and stops when its client
/// no longer renews it, e.g. after the desktop app crashed.
/// </summary>
/// <param name="gate">The daemon's gate, which a dictation, a provider test and an engine swap also hold.</param>
/// <param name="background">Runs the expiry loop and reports its failure.</param>
/// <param name="stopping">Cancelled when the daemon stops.</param>
internal sealed class LevelMonitor(
    SemaphoreSlim gate,
    Func<IDictationEngine> engine,
    TimeSpan lifetime,
    Action<Task> background,
    CancellationToken stopping)
{
    /// <summary>The expiry check runs a few times per lifetime, within these bounds.</summary>
    private const int ChecksPerLifetime = 4;
    private const double MinCheckMs = 20;
    private const double MaxCheckMs = 1000;

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

            _until = DateTimeOffset.UtcNow + lifetime;
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
        var step = TimeSpan.FromMilliseconds(Math.Clamp(lifetime.TotalMilliseconds / ChecksPerLifetime, MinCheckMs, MaxCheckMs));
        while (_monitoring)
        {
            await Task.Delay(step, stopping).ConfigureAwait(false);
            if (_monitoring && DateTimeOffset.UtcNow >= _until)
            {
                await gate.WaitAsync(stopping).ConfigureAwait(false);
                try
                {
                    if (DateTimeOffset.UtcNow >= _until)
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
