using System.CommandLine;
using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Core.Processing;

namespace Dictum.Cli.Commands;

internal static class ProcessCommand
{
    /// <summary>The profile id a prompt sees when no profile is configured.</summary>
    private const string NoProfileId = "cli";

    public static Command Create()
    {
        var text = new Argument<string?>("text")
        {
            Description = "Text to process, or - (or nothing) to read stdin",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var pipeline = new Option<string?>("--pipeline") { Description = "Comma-separated processor ids (default: the profile's chain)" };
        var profile = new Option<string?>("--profile", "-p") { Description = "Profile whose chain and language to use" };
        var language = new Option<string?>("--lang", "-l") { Description = "Language of the text, passed to LLM prompts" };
        var json = new Option<bool>("--json") { Description = "Print a JSON object with the text and per-processor timings" };

        var command = new Command("process", "Run text through processors, e.g. dictum transcribe memo.wav --raw | dictum process --pipeline cleanup")
        {
            text,
            pipeline,
            profile,
            language,
            json,
        };

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var input = parse.GetValue(text) is { } value && value != "-"
                ? value
                : await Console.In.ReadToEndAsync(token).ConfigureAwait(false);
            input = input.Trim();

            var host = context.Host();
            var config = host.Config;
            var chosen = parse.GetValue(profile) is { } id
                ? config.Profiles.FirstOrDefault(p => p.Id == id) ?? throw new ConfigException($"Unknown profile '{id}'.")
                : config.Profiles.FirstOrDefault();
            var steps = TranscriptionOptions.SplitList(parse.GetValue(pipeline)) ?? chosen?.Pipeline ?? [];
            if (steps.Count == 0)
            {
                throw new ConfigException("No processors to run: pass --pipeline <ids> or use a profile with a pipeline.");
            }

            var ctx = chosen is null
                ? new ProcessingContext(NoProfileId, parse.GetValue(language), ActiveApp: null)
                : ProcessingContext.ForProfile(chosen, parse.GetValue(language));
            var stages = new StageList();
            var result = await ProcessingChain.RunAsync(steps.Select(host.GetProcessor), input, ctx, stages, token).ConfigureAwait(false);

            context.Out.WriteLine(parse.GetValue(json)
                ? JsonSerializer.Serialize(new ProcessJson(result, input, stages.Stages), CliJsonContext.Readable.ProcessJson)
                : result);
            return ExitCodes.Success;
        }, ct));

        return command;
    }

    private sealed class StageList : IProcessingObserver
    {
        public List<StageJson> Stages { get; } = [];

        public void Starting(string stage)
        {
        }

        public void Finished(StageTiming timing, string text) => Stages.Add(StageJson.From(timing));
    }
}
