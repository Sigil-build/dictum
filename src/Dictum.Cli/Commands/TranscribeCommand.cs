using System.CommandLine;
using Dictum.Audio;

namespace Dictum.Cli.Commands;

internal static class TranscribeCommand
{
    public static Command Create()
    {
        var file = new Argument<string>("file") { Description = "WAV file to transcribe, or - for stdin" };
        var options = new TranscriptionOptions();
        var command = new Command("transcribe", "Transcribe a WAV file (any rate or channel count)") { file };
        options.AddTo(command);

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var path = parse.GetValue(file)!;
            var clip = path == "-"
                ? Wav.ReadClip(Console.OpenStandardInput())
                : ReadFile(path);

            var host = context.Host();
            var profile = host.ResolveProfile(options.Read(parse));
            var result = await host.CreatePipeline().RunAsync(clip, profile, output: null, token).ConfigureAwait(false);
            return Results.Print(context, result, parse.GetValue(options.Json));
        }, ct));

        return command;
    }

    private static Core.Audio.AudioClip ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Wav.ReadClip(stream);
    }
}
