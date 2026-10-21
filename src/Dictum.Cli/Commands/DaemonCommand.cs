using System.CommandLine;
using Dictum.Core.Diagnostics;
using Dictum.Host.Daemon;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.Cli.Commands;

internal static class DaemonCommand
{
    public static Command Create()
    {
        var install = new Option<bool>("--install-autostart") { Description = "Start the daemon at login, then exit" };
        var uninstall = new Option<bool>("--uninstall-autostart") { Description = "Remove every Dictum login entry (the desktop app's or the daemon's), then exit" };
        var command = new Command(DictumExecutables.DaemonVerb, "Run in the background: hotkeys, dictation and the control socket (Ctrl+C stops)")
        {
            install,
            uninstall,
        };

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, token) =>
            parse.GetValue(install) || parse.GetValue(uninstall)
                ? ConfigureAutostartAsync(context, parse.GetValue(install), token)
                : ServeAsync(context, token), ct, consoleLevel: LogLevel.Info));

        return command;
    }

    private static async Task<int> ServeAsync(CliContext context, CancellationToken ct)
    {
        var (config, warnings) = context.Store.LoadWithIssues();
        context.UseConfig(config);
        await DaemonHost.RunAsync(
            new DaemonHostOptions(
                config,
                warnings,
                context.Store,
                context.Paths,
                context.SocketPath,
                context.Platform,
                context.Log,
                context.CreateHost,
                context.UseConfig,
                status => PrintSummary(context, status)),
            ct).ConfigureAwait(false);
        return ExitCodes.Success;
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
            await autostart.InstallAsync(InstallLocation.Daemon(executable, InstallLocation.AppImage), ct).ConfigureAwait(false);
            context.Out.WriteLine(autostart.Location(LoginApp.Daemon));
            context.Error.WriteLine("The daemon will start at your next login.");
        }
        else
        {
            await autostart.UninstallAsync(ct).ConfigureAwait(false);
            context.Error.WriteLine("Dictum no longer starts at login.");
        }

        return ExitCodes.Success;
    }
}
