using System.CommandLine;
using Dictum.Core.Configuration;

namespace Dictum.Cli.Commands;

internal static class ProfilesCommand
{
    public static Command Create()
    {
        var command = new Command("profiles", "List and inspect profiles");

        var list = new Command("list", "List profiles");
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, (context, _) =>
        {
            foreach (var profile in context.LoadConfig().Profiles)
            {
                context.Out.WriteLine($"{profile.Id,-20} {profile.Hotkey ?? "-",-18} {profile.Mode,-11} stt={profile.Stt} lang={profile.Language ?? "auto"} pipeline=[{string.Join(", ", profile.Pipeline)}]");
            }

            return Task.FromResult(ExitCodes.Success);
        }));

        var id = new Argument<string>("id") { Description = "Profile id" };
        var show = new Command("show", "Show a profile with its provider and processors") { id };
        show.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, (context, _) =>
        {
            var config = context.LoadConfig();
            var wanted = parse.GetValue(id);
            var profile = config.Profiles.FirstOrDefault(p => p.Id == wanted)
                ?? throw new ConfigException($"Unknown profile '{wanted}'.");

            var o = context.Out;
            o.WriteLine($"id:         {profile.Id}");
            o.WriteLine($"name:       {profile.Name ?? "-"}");
            o.WriteLine($"hotkey:     {profile.Hotkey ?? "-"} ({profile.Mode})");
            o.WriteLine($"stt:        {profile.Stt}  {(config.Stt.TryGetValue(profile.Stt, out var stt) ? SttCommand.Describe(stt) : "(missing)")}");
            o.WriteLine($"language:   {profile.Language ?? "auto"}");
            o.WriteLine($"sttPrompt:  {profile.SttPrompt ?? "-"}");
            o.WriteLine($"pipeline:   {(profile.Pipeline.Count == 0 ? "-" : string.Join(" → ", profile.Pipeline))}");
            foreach (var step in profile.Pipeline)
            {
                o.WriteLine($"  {step,-16} {(config.Processors.TryGetValue(step, out var p) ? Describe(p) : "(missing)")}");
            }

            o.WriteLine($"output:     {profile.Output.ToString().ToLowerInvariant()}");
            return Task.FromResult(ExitCodes.Success);
        }));

        command.Add(list);
        command.Add(show);
        return command;
    }

    private static string Describe(ProcessorConfig config) => config switch
    {
        ReplaceProcessorConfig r => $"{ReplaceProcessorConfig.TypeName}, {r.Rules.Count} rules",
        LlmProcessorConfig l => $"{LlmProcessorConfig.TypeName}, {l.BaseUrl} model={l.Model} prompt={l.Prompt}",
        _ => config.GetType().Name,
    };
}
