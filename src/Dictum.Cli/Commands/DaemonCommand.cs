using System.CommandLine;
using System.Runtime.InteropServices;
using Dictum.Audio.PortAudio;
using Dictum.Core.Diagnostics;
using Dictum.Host.Daemon;
using Dictum.Host.Diagnostics;
using Dictum.Host;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.Cli.Commands;

internal static class DaemonCommand
{
    public static Command Create()
    {
        var install = new Option<bool>("--install-autostart") { Description = "Start the daemon at login, then exit" };
        var uninstall = new Option<bool>("--uninstall-autostart") { Description = "Stop starting the daemon at login, then exit" };
        var command = new Command("daemon", "Run in the background: hotkeys, dictation and the control socket (Ctrl+C stops)")
        {
            install,
            uninstall,
        };

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            if (parse.GetValue(install) || parse.GetValue(uninstall))
            {
                return await ConfigureAutostartAsync(context, parse.GetValue(install), token).ConfigureAwait(false);
            }

            var (config, warnings) = context.Store.LoadWithIssues();
            context.UseConfig(config);
            var platform = context.Platform;
            var log = context.Log;

            var events = new IpcEventHub();
            await using var daemon = new DictumDaemon(config, context.CreateHost, platform, new SoundPlayer(log), log, events, warnings);
            await using var server = new IpcServer(context.SocketPath, log, events);
            await server.StartAsync(daemon.HandleAsync, token).ConfigureAwait(false);
            await daemon.StartAsync(token).ConfigureAwait(false);
            using var watcher = new ConfigWatcher(
                context.Store,
                async (changed, changedWarnings) =>
                {
                    context.UseConfig(changed);
                    await daemon.ApplyConfigAsync(changed, token, changedWarnings).ConfigureAwait(false);
                },
                invalid => platform.Notifier.NotifyAsync("Dictum: config not applied", invalid.Message, NotificationKind.Error, token),
                log);

            daemon.ReloadHandler = async c =>
            {
                var (fresh, freshWarnings) = context.Store.LoadWithIssues();
                context.UseConfig(fresh);
                await daemon.ApplyConfigAsync(fresh, c, freshWarnings).ConfigureAwait(false);
            };

            // The daemon's own checks use its live engine: the Whisper model is already loaded, so the stt rows are cheap.
            daemon.DoctorHandler = c => DoctorChecks.RunAsync(new DoctorInputs(
                context.ConfigPath,
                daemon.Config,
                null,
                daemon.ConfigWarnings,
                context.Paths,
                platform,
                HardwareProbe.Detect(context.Paths),
                () => daemon.Engine.Audio,
                id => daemon.Engine.GetStt(id),
                id => daemon.Engine.GetProcessor(id),
                _ => Task.FromResult(HealthStatus.Ok($"running (pid {Environment.ProcessId}), answering this check"))), c);

            PrintSummary(context, daemon.Status());

            var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                terminated.TrySetResult();
            });
            await Task.WhenAny(Task.Delay(Timeout.Infinite, token), terminated.Task, daemon.ShutdownRequested).ConfigureAwait(false);

            log.Info("Daemon stopping");
            return ExitCodes.Success;
        }, consoleLevel: LogLevel.Info));

        return command;
    }

    private static void PrintSummary(CliContext context, DaemonStatus status)
    {
        var e = context.Error;
        e.WriteLine($"Dictum daemon running (pid {status.ProcessId}). Ctrl+C stops it.");
        e.WriteLine($"  hotkeys: {status.HotkeyBackend}");
        e.WriteLine($"  paste:   {status.InjectorBackend}");
        foreach (var profile in status.Profiles)
        {
            var hotkey = profile.Hotkey is null ? "no hotkey" : $"{profile.Hotkey} ({profile.Mode}{(profile.Registered ? "" : ", not registered")})";
            e.WriteLine($"  {profile.Id,-16} {hotkey}");
        }

        if (status.Warning is not null)
        {
            e.WriteLine($"warning: {status.Warning}");
        }
    }

    private static async Task<int> ConfigureAutostartAsync(CliContext context, bool install, CancellationToken ct)
    {
        var autostart = context.Platform.Autostart
            ?? throw new InvalidOperationException($"Autostart is not supported on {context.Platform.Name} yet.");
        if (install)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the dictum executable path.");
            await autostart.InstallAsync(executable, ct).ConfigureAwait(false);
            context.Out.WriteLine(autostart.Location);
            context.Error.WriteLine("The daemon will start at your next login.");
        }
        else
        {
            await autostart.UninstallAsync(ct).ConfigureAwait(false);
            context.Error.WriteLine($"Removed {autostart.Location}.");
        }

        return ExitCodes.Success;
    }
}
