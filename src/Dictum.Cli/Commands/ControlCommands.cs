using System.CommandLine;
using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Host.Ipc;

namespace Dictum.Cli.Commands;

/// <summary>The commands that talk to a running daemon, from <c>toggle</c> to <c>status</c>.</summary>
internal static class ControlCommands
{
    public static IEnumerable<Command> Create()
    {
        yield return Control("toggle", "Start dictation, or stop it and paste the text (bind this in your compositor)");
        yield return Control("start", "Start dictation");
        yield return Control("stop", "Stop dictation and paste the text");
        yield return Control("cancel", "Stop dictation and discard it");
        yield return Control("reload", "Re-read the config file now");
        yield return Control("shutdown", "Stop the running daemon");
        yield return Status();
    }

    private static Command Control(string name, string description)
    {
        var profile = new Option<string?>("--profile", "-p") { Description = "Profile id (default: the active or first profile)" };
        var command = new Command(name, description) { profile };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            var response = await new IpcClient(context.SocketPath).SendAsync(new IpcRequest(name, parse.GetValue(profile)), token).ConfigureAwait(false);
            if (!response.Ok)
            {
                context.Error.WriteLine($"error: {response.Error}");
                return ExitCodes.Error;
            }

            var status = response.Status!;
            context.Error.WriteLine(status.ActiveProfile is null ? status.State : $"{status.State} ({status.ActiveProfile})");
            return ExitCodes.Success;
        }));
        return command;
    }

    private static Command Status()
    {
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var watch = new Option<bool>("--watch") { Description = "Keep printing state changes, results and status as JSON lines until Ctrl+C" };
        var frames = new Option<bool>("--frames") { Description = "With --watch, also print audio frames (50 per second)" };
        var command = new Command("status", "Show whether the daemon runs, its state, hotkeys and backends") { json, watch, frames };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            if (parse.GetValue(watch))
            {
                await using var subscription = await new IpcClient(context.SocketPath).SubscribeAsync(token).ConfigureAwait(false);
                await foreach (var message in subscription.ReadEventsAsync(token).ConfigureAwait(false))
                {
                    if (message.Event != "frame" || parse.GetValue(frames))
                    {
                        context.Out.WriteLine(JsonSerializer.Serialize(message, CliJsonContext.Readable.IpcEvent));
                    }
                }

                return ExitCodes.Success;
            }

            var response = await new IpcClient(context.SocketPath).SendAsync(new IpcRequest("status"), token).ConfigureAwait(false);
            var status = response.Status!;
            if (parse.GetValue(json))
            {
                context.Out.WriteLine(JsonSerializer.Serialize(status, CliJsonContext.Readable.DaemonStatus));
                return ExitCodes.Success;
            }

            var o = context.Out;
            o.WriteLine($"daemon:  running (pid {status.ProcessId}), {status.State}{(status.ActiveProfile is null ? "" : $" ({status.ActiveProfile})")}");
            o.WriteLine($"hotkeys: {status.HotkeyBackend}");
            o.WriteLine($"paste:   {status.InjectorBackend}");
            foreach (var p in status.Profiles)
            {
                o.WriteLine($"  {p.Id,-16} {p.Hotkey ?? "-",-18} {p.Mode,-11} {(p.Hotkey is null ? "" : p.Registered ? "registered" : "NOT registered")}");
            }

            if (status.Warning is not null)
            {
                o.WriteLine($"warning: {status.Warning}");
            }

            return ExitCodes.Success;
        }));
        return command;
    }
}
