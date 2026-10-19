using System.CommandLine;
using Dictum.Core.Configuration;
using Dictum.Core.Hotkeys;

namespace Dictum.Cli.Commands;

internal static class ProfilesCommand
{
    public static Command Create()
    {
        var command = new Command("profiles", "List and inspect profiles");

        var list = new Command("list", "List profiles");
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            foreach (var profile in context.LoadConfig().Profiles)
            {
                context.Out.WriteLine($"{profile.Id,-20} {profile.Hotkey ?? "-",-18} {profile.Mode,-11} stt={profile.Stt} lang={profile.Language ?? LanguageList.Auto} pipeline=[{string.Join(", ", profile.Pipeline)}]");
            }

            return Task.FromResult(ExitCodes.Success);
        }, ct));

        var id = new Argument<string>("id") { Description = "Profile id" };
        var show = new Command("show", "Show a profile with its provider and processors") { id };
        show.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
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
            o.WriteLine($"language:   {profile.Language ?? LanguageList.Auto}");
            o.WriteLine($"sttPrompt:  {profile.SttPrompt ?? "-"}");
            o.WriteLine($"vocabulary: {(profile.Vocabulary.Count == 0 ? "-" : string.Join(", ", profile.Vocabulary))}");
            o.WriteLine($"pipeline:   {(profile.Pipeline.Count == 0 ? "-" : string.Join(" → ", profile.Pipeline))}");
            foreach (var step in profile.Pipeline)
            {
                o.WriteLine($"  {step,-16} {(config.Processors.TryGetValue(step, out var p) ? Describe(p) : "(missing)")}");
            }

            o.WriteLine($"output:     {profile.Output.ToString().ToLowerInvariant()}");
            return Task.FromResult(ExitCodes.Success);
        }, ct));

        var hotkeyProfile = new Argument<string>("id") { Description = "Profile id" };
        var hotkeyValue = new Argument<string>("hotkey") { Description = "Combination, e.g. \"Ctrl+Alt+Space\" or \"Ctrl+Alt+`\"" };
        var setHotkey = new Command("set-hotkey", "Change a profile's hotkey; a running daemon picks it up at once") { hotkeyProfile, hotkeyValue };
        setHotkey.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var config = context.LoadConfig();
            var id = parse.GetValue(hotkeyProfile);
            var index = config.Profiles.FindIndex(p => p.Id == id);
            if (index < 0)
            {
                throw new ConfigException($"Unknown profile '{id}'.");
            }

            if (!Hotkey.TryParse(parse.GetValue(hotkeyValue), out var hotkey, out var error))
            {
                throw new ConfigException($"Invalid hotkey: {error}.");
            }

            config.Profiles[index] = config.Profiles[index] with { Hotkey = hotkey.ToString() };
            context.SaveValidated(config);
            context.Error.WriteLine($"{id}: {hotkey}");
            return Task.FromResult(ExitCodes.Success);
        }, ct));

        command.Add(list);
        command.Add(setHotkey);
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
