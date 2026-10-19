using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

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
            return IpcResponse.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A provider that is down or a server that times out must not look like a dead daemon to the client.
            log.Error($"IPC '{request.Command}' failed: {ex.Message}", ex);
            return IpcResponse.Failure(DictumDaemon.MessageOf(ex));
        }
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
