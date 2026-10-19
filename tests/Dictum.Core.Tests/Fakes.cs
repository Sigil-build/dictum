using Dictum.Core.Audio;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Core.Processing;
using Dictum.Core.Stt;

namespace Dictum.Core.Tests;

internal sealed class FakeStt(string text, string? language = "uk") : ISpeechToText
{
    public string Id => "fake-stt";

    public SttRequest? LastRequest { get; private set; }

    public Exception? Throw { get; init; }

    public Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct)
    {
        LastRequest = request;
        return Throw is not null
            ? Task.FromException<Transcript>(Throw)
            : Task.FromResult(new Transcript(text, language, TimeSpan.FromMilliseconds(5), "fake"));
    }

    public Task<HealthStatus> CheckAsync(CancellationToken ct) => Task.FromResult(HealthStatus.Ok("fake"));
}

internal sealed class FakeProcessor(string id, Func<string, string> transform) : ITextProcessor
{
    public string Id => id;

    public ProcessingContext? LastContext { get; private set; }

    public Task<string> ProcessAsync(string text, ProcessingContext context, CancellationToken ct)
    {
        LastContext = context;
        return Task.FromResult(transform(text));
    }
}

internal sealed class CollectingOutput : ITextOutput
{
    public List<string> Written { get; } = [];

    public Task WriteAsync(string text, CancellationToken ct)
    {
        Written.Add(text);
        return Task.CompletedTask;
    }
}

internal static class Clips
{
    public static AudioClip Seconds(double seconds) => new(new float[(int)(seconds * AudioClip.WhisperSampleRate)]);
}
