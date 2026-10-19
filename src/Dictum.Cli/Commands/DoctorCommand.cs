using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Diagnostics;

namespace Dictum.Cli.Commands;

internal static class DoctorCommand
{
    /// <summary>Width of the check name column; hints line up under the details.</summary>
    private const int NameWidth = 22;

    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var command = new Command("doctor", "Check config, microphone and every provider, with hints for anything broken") { json };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
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

            var results = await DoctorChecks.RunAsync(
                DoctorInputs.ForCli(context.ConfigPath, config, error, warnings, context.Paths, context.Platform, context.Host, context.SocketPath),
                token).ConfigureAwait(false);
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
                    context.Out.WriteLine($"{Label(check.Level)} {check.Name,-NameWidth} {check.Detail}");
                    if (check.Hint is not null && check.Level != HealthLevel.Ok)
                    {
                        context.Out.WriteLine($"     {"",-NameWidth} → {check.Hint}");
                    }
                }
            }

            return checks.Any(c => c.Level == HealthLevel.Error) ? ExitCodes.Error : ExitCodes.Success;
        }, ct));

        return command;
    }

    private static string Label(HealthLevel level) => level switch
    {
        HealthLevel.Ok => "  OK",
        HealthLevel.Warning => "WARN",
        _ => " ERR",
    };
}
