using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Plugins;

namespace Dictum.Host.Daemon;

/// <summary>Answers each IPC command; the errors a client should see become failure answers.</summary>
internal sealed class DaemonCommandRouter(
    DictumDaemon daemon,
    HotkeyRegistrar hotkeys,
    SttTester tester,
    LevelMonitor monitor,
    DaemonHostCommands? host,
    ILog log)
{
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            return await DispatchAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ConfigException or InvalidOperationException or AudioDeviceException)
        {
            return IpcResponse.Failure(FailureOf(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A provider that is down or a server that times out must not look like a dead daemon to the client.
            if (IsExpected(ex))
            {
                // A plugin that cannot start, such as one not confirmed: a warning as the plugin host logs it, with its
                // hint and last lines and no stack trace.
                log.Warn($"IPC '{request.Command}' failed: {FailureOf(ex)}");
            }
            else
            {
                log.Error($"IPC '{request.Command}' failed: {ex.Message}", ex);
            }

            return IpcResponse.Failure(FailureOf(ex), KindOf(ex));
        }
    }

    private static bool IsExpected(Exception ex) =>
        (ex is DictationException { InnerException: { } inner } ? inner : ex) is PluginUnavailableException;

    /// <summary>What a client may act on in a failure: that a provider, or the plugin it runs, could not start or answer.</summary>
    internal static string? KindOf(Exception ex) =>
        (ex is DictationException { InnerException: { } inner } ? inner : ex) is PluginUnavailableException or ProviderUnavailableException
            ? IpcFailureKind.ProviderUnavailable
            : null;

    /// <summary>
    /// The failure a client shows, which has no field for a hint: the cause's message, its hint after it, and a
    /// plugin's last lines after both, as doctor's row quotes them.
    /// </summary>
    internal static string FailureOf(Exception ex)
    {
        var cause = ex is DictationException { InnerException: { } inner } ? inner : ex;
        var text = cause is IHasHint { Hint: { Length: > 0 } hint } ? $"{cause.Message} {hint}" : cause.Message;
        return cause is PluginUnavailableException plugin ? plugin.WithOutput(text) : text;
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        switch (request.Command)
        {
            case IpcCommand.Status:
            case IpcCommand.Subscribe:
                break;
            case IpcCommand.Start:
                await daemon.StartDictationAsync(daemon.ProfileFor(request.Profile), ct).ConfigureAwait(false);
                break;
            case IpcCommand.Stop:
                daemon.StopActiveDictation();
                break;
            case IpcCommand.Toggle:
                await daemon.ToggleAsync(request.Profile, ct).ConfigureAwait(false);
                break;
            case IpcCommand.Cancel:
                await daemon.CancelAsync().ConfigureAwait(false);
                break;
            case IpcCommand.Reload:
                if (host is null)
                {
                    return IpcResponse.Failure("This daemon cannot reload its config.");
                }

                var (fresh, freshWarnings) = host.Reload();
                await daemon.ApplyConfigAsync(fresh, ct, freshWarnings).ConfigureAwait(false);
                break;
            case IpcCommand.Shutdown:
                await daemon.ShutdownAsync().ConfigureAwait(false);
                break;
            case IpcCommand.PauseHotkeys:
            case IpcCommand.ResumeHotkeys:
                await hotkeys.SetPausedAsync(request.Command == IpcCommand.PauseHotkeys, ct).ConfigureAwait(false);
                break;
            case IpcCommand.UseProfile:
                daemon.SelectProfile(request.Profile);
                break;
            case IpcCommand.Devices:
                return new IpcResponse(true, Status: daemon.Status(), Devices: daemon.Engine.Audio.ListDevices());
            case IpcCommand.Doctor:
                if (host is null)
                {
                    return IpcResponse.Failure("This daemon cannot run the checks.");
                }

                return new IpcResponse(true, Status: daemon.Status(), Checks: await host.Doctor(daemon, ct).ConfigureAwait(false));
            case IpcCommand.TestStt:
                return new IpcResponse(true, Status: daemon.Status(), Test: await tester.TestAsync(request, ct).ConfigureAwait(false));
            case IpcCommand.MonitorStart:
                await monitor.StartAsync(request.Device, ct).ConfigureAwait(false);
                break;
            case IpcCommand.MonitorStop:
                await monitor.StopAsync(ct).ConfigureAwait(false);
                break;
            default:
                return IpcResponse.Failure($"Unknown command '{request.Command}'.");
        }

        return IpcResponse.Success(daemon.Status());
    }
}
