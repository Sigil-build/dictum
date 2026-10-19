using System.CommandLine;
using Dictum.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Stt;

namespace Dictum.Cli.Commands;

internal static class SttCommand
{
    private const int RecordSeconds = 5;

    public static Command Create()
    {
        var command = new Command("stt", "List and test speech-to-text providers");
        command.Add(List());
        command.Add(Test());
        return command;
    }

    private static Command List()
    {
        var command = new Command("list", "List configured STT providers");
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var config = context.LoadConfig();
            foreach (var (id, stt) in config.Stt)
            {
                context.Out.WriteLine($"{id,-20} {Describe(stt)}");
            }

            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return command;
    }

    private static Command Test()
    {
        var id = new Argument<string>("id") { Description = "Provider id" };
        var file = new Option<string?>("--file", "-f") { Description = $"WAV file to transcribe instead of recording {RecordSeconds} seconds" };
        var language = new Option<string?>("--lang", "-l") { Description = "Language code, a list such as \"uk, en\" to pick from, or auto" };
        var command = new Command("test", "Check a provider and transcribe a sample with it") { id, file, language };

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            context.LoadConfig();
            var stt = context.Host().GetStt(parse.GetValue(id)!);

            var status = await stt.CheckAsync(token).ConfigureAwait(false);
            context.Error.WriteLine($"{status.Level}: {status.Detail}");
            if (status.Hint is not null)
            {
                context.Error.WriteLine($"  hint: {status.Hint}");
            }

            if (status.Level == HealthLevel.Error)
            {
                return ExitCodes.ProviderUnavailable;
            }

            Core.Audio.AudioClip clip;
            if (parse.GetValue(file) is { } path)
            {
                await using var stream = File.OpenRead(path);
                clip = Wav.ReadClip(stream);
            }
            else
            {
                var audio = context.Host().Audio;
                context.Error.WriteLine($"Recording {RecordSeconds} seconds… say something.");
                await audio.StartAsync(context.TryLoadConfig()?.General.AudioDevice, token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(RecordSeconds), token).ConfigureAwait(false);
                clip = await audio.StopAsync(token).ConfigureAwait(false);
            }

            var request = SttRequest.ForSetting(parse.GetValue(language), null)
                ?? throw new ConfigException($"'{parse.GetValue(language)}' is not an ISO 639-1 language code, a comma-separated list of codes, or 'auto'.");
            var transcript = await stt.TranscribeAsync(clip, request, token).ConfigureAwait(false);
            context.Out.WriteLine(transcript.Text);
            context.Error.WriteLine(
                $"{clip.Duration.TotalSeconds:F1} s of audio in {transcript.Elapsed.TotalMilliseconds:F0} ms" +
                $" (language {transcript.Language ?? "?"}, backend {transcript.Backend ?? "?"})");
            return ExitCodes.Success;
        }, ct));

        return command;
    }

    public static string Describe(SttConfig config) => config switch
    {
        WhisperLocalSttConfig w => $"{WhisperLocalSttConfig.TypeName}  model={w.Model} runtime={w.Runtime.ToString().ToLowerInvariant()} vad={w.Vad.ToString().ToLowerInvariant()}",
        OpenAiCompatibleSttConfig o => $"{OpenAiCompatibleSttConfig.TypeName}  {o.BaseUrl} model={o.Model}",
        _ => config.GetType().Name,
    };
}
