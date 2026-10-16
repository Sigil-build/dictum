using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core;
using Dictum.Host;
using Dictum.Host.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var command = new Command("doctor", "Check config, microphone and every provider, with hints for anything broken") { json };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            DictumConfig? config = null;
            ConfigException? error = null;
            IReadOnlyList<ConfigIssue> warnings = [];
            try
            {
                config = context.TryLoadConfig();
                warnings = config is null ? [] : context.Store.LoadWithIssues().Warnings;
            }
            catch (ConfigException ex)
            {
                error = ex;
            }

            var results = await DoctorChecks.RunAsync(new DoctorInputs(
                context.ConfigPath,
                config,
                error,
                warnings,
                context.Paths,
                context.Platform,
                HardwareProbe.Detect(context.Paths),
                () => context.Host().Audio,
                id => context.Host().GetStt(id),
                id => context.Host().GetProcessor(id),
                c => CheckDaemonAsync(context, c)), token).ConfigureAwait(false);
            var checks = results.Select(c => new CheckJson(c.Name, c.Status.Level, c.Status.Detail, c.Status.Hint)).ToList();

            if (parse.GetValue(json))
            {
                var report = new DoctorJson(CliApp.Version, RuntimeInformation.OSDescription, RuntimeInformation.RuntimeIdentifier, checks);
                context.Out.WriteLine(JsonSerializer.Serialize(report, CliJsonContext.Readable.DoctorJson));
            }
            else
            {
                context.Out.WriteLine($"dictum {CliApp.Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})");
                foreach (var check in checks)
                {
                    context.Out.WriteLine($"{Label(check.Level)} {check.Name,-22} {check.Detail}");
                    if (check.Hint is not null && check.Level != HealthLevel.Ok)
                    {
                        context.Out.WriteLine($"     {"",-22} → {check.Hint}");
                    }
                }
            }

            return checks.Any(c => c.Level == HealthLevel.Error) ? ExitCodes.Error : ExitCodes.Success;
        }));

        return command;
    }

    private static async Task<HealthStatus> CheckDaemonAsync(CliContext context, CancellationToken ct)
    {
        try
        {
            var status = (await new IpcClient(context.SocketPath).SendAsync(new IpcRequest("status"), ct).ConfigureAwait(false)).Status!;
            var registered = status.Profiles.Count(p => p.Registered);
            var detail = $"running (pid {status.ProcessId}), {status.State}; {registered} hotkey(s) registered via {status.HotkeyBackend}";
            return status.Warning is null ? HealthStatus.Ok(detail) : HealthStatus.Warning(detail, status.Warning);
        }
        catch (DaemonNotRunningException)
        {
            return HealthStatus.Warning("not running", "Start it with 'dictum daemon', or at login with 'dictum daemon --install-autostart'.");
        }
    }

    private static string Label(HealthLevel level) => level switch
    {
        HealthLevel.Ok => "  OK",
        HealthLevel.Warning => "WARN",
        _ => " ERR",
    };
}
