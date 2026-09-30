using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;

namespace Dictum.Host.Daemon;

/// <summary>What the daemon needs from a configured host; one engine per config snapshot.</summary>
public interface IDictationEngine : IAsyncDisposable
{
    DictumConfig Config { get; }

    DictationPipeline Pipeline { get; }

    DictationProfile ResolveProfile(string profileId);

    /// <summary>Loads models and checks providers so the first dictation is not a cold start.</summary>
    Task WarmUpAsync(CancellationToken ct);
}
