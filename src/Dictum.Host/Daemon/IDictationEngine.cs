using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Core.Processing;
using Dictum.Core.Stt;

namespace Dictum.Host.Daemon;

/// <summary>What the daemon needs from a configured host; one engine per config snapshot.</summary>
public interface IDictationEngine : IAsyncDisposable
{
    DictumConfig Config { get; }

    DictationPipeline Pipeline { get; }

    /// <summary>The microphone the pipeline records from; frames for the overlay come from here.</summary>
    IAudioSource Audio { get; }

    ISpeechToText GetStt(string id);

    ITextProcessor GetProcessor(string id);

    DictationProfile ResolveProfile(string profileId);

    /// <summary>Loads models and checks providers so the first dictation is not a cold start.</summary>
    Task WarmUpAsync(CancellationToken ct);
}
