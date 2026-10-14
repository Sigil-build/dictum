using System.CommandLine;
using Dictum.Core.Configuration;
using Dictum.Core.Vocabulary;

namespace Dictum.Cli.Commands;

/// <summary>Edits a profile's word list; a running daemon picks the change up at once.</summary>
internal static class VocabCommand
{
    private static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Profile id (default: the first profile)",
        Recursive = true,
    };

    public static Command Create()
    {
        var command = new Command("vocab", "Words, names and terms the recognizer should spell exactly, per profile") { ProfileOption };

        var list = new Command("list", "Print the profile's words, one per line");
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, (context, _) =>
        {
            var config = context.LoadConfig();
            var profile = config.Profiles[IndexOf(config, parse.GetValue(ProfileOption))];
            foreach (var term in profile.Vocabulary)
            {
                context.Out.WriteLine(term);
            }

            WarnAboutDroppedTerms(context, profile);
            return Task.FromResult(ExitCodes.Success);
        }));

        var addTerms = new Argument<string[]>("terms") { Description = "Words or phrases; quote phrases with spaces", Arity = ArgumentArity.OneOrMore };
        var add = new Command("add", "Add words to the end of the list") { addTerms };
        add.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, (context, _) =>
            Task.FromResult(Edit(context, parse.GetValue(ProfileOption), current => VocabularyList.Merge(current, parse.GetValue(addTerms) ?? [])))));

        var removeTerms = new Argument<string[]>("terms") { Description = "Words or phrases to remove (any letter case)", Arity = ArgumentArity.OneOrMore };
        var remove = new Command("remove", "Remove words from the list") { removeTerms };
        remove.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, (context, _) =>
            Task.FromResult(Edit(context, parse.GetValue(ProfileOption), current =>
            {
                var (remaining, notFound) = VocabularyList.Remove(current, parse.GetValue(removeTerms) ?? []);
                if (notFound.Count > 0)
                {
                    context.Error.WriteLine($"warning: not in the list: {string.Join(", ", notFound)}");
                }

                return remaining;
            }))));

        var clear = new Command("clear", "Remove every word from the list");
        clear.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, (context, _) =>
            Task.FromResult(Edit(context, parse.GetValue(ProfileOption), _ => []))));

        var file = new Argument<string>("file") { Description = "Text file with one word or phrase per line ('#' starts a comment), or - for stdin" };
        var import = new Command("import", "Add words from a file") { file };
        import.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            var path = parse.GetValue(file)!;
            var text = path == "-"
                ? await Console.In.ReadToEndAsync(token).ConfigureAwait(false)
                : await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
            var terms = VocabularyList.ParseLines(text).ToList();
            return Edit(context, parse.GetValue(ProfileOption), current => VocabularyList.Merge(current, terms));
        }));

        command.Add(list);
        command.Add(add);
        command.Add(remove);
        command.Add(clear);
        command.Add(import);
        return command;
    }

    private static int Edit(CliContext context, string? profileId, Func<List<string>, List<string>> change)
    {
        var config = context.LoadConfig();
        var index = IndexOf(config, profileId);
        var updated = config.Profiles[index] with { Vocabulary = change(config.Profiles[index].Vocabulary) };
        config.Profiles[index] = updated;

        var errors = ConfigValidator.Validate(config).Where(i => i.Severity == ConfigIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new ConfigException("The change would make the config invalid.", errors);
        }

        context.Store.Save(config);
        context.Error.WriteLine($"{updated.Id}: {updated.Vocabulary.Count} word(s)");
        WarnAboutDroppedTerms(context, updated);
        return ExitCodes.Success;
    }

    private static int IndexOf(DictumConfig config, string? profileId)
    {
        if (config.Profiles.Count == 0)
        {
            throw new ConfigException("The config has no profiles. Run 'dictum init'.");
        }

        if (profileId is null)
        {
            return 0;
        }

        var index = config.Profiles.FindIndex(p => p.Id == profileId);
        return index >= 0 ? index : throw new ConfigException($"Unknown profile '{profileId}'.");
    }

    private static void WarnAboutDroppedTerms(CliContext context, ProfileConfig profile)
    {
        var dropped = SttPromptBuilder.Build(profile.SttPrompt, VocabularyList.Normalize(profile.Vocabulary)).Dropped;
        if (dropped.Count > 0)
        {
            context.Error.WriteLine(
                $"warning: the speech hint holds {SttPromptBuilder.MaxLength} characters, so these words are left out: "
                + $"{string.Join(", ", dropped)}. Put the most important words first.");
        }
    }
}
