using Dictum.Core.Audio;
using Dictum.Core.Diagnostics;

namespace Dictum.Core.Stt;

public interface ISpeechToText
{
    /// <summary>Provider instance id from the config, e.g. "local-turbo".</summary>
    string Id { get; }

    SttCapabilities Capabilities { get; }

    Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct);

    /// <summary>Used by <c>dictum doctor</c> and the "Check" button.</summary>
    Task<HealthStatus> CheckAsync(CancellationToken ct);
}

public sealed record SttCapabilities(bool LanguageDetection, bool Prompt, bool Streaming);

/// <param name="Language">ISO 639-1 code, or null to auto-detect.</param>
/// <param name="Prompt">Vocabulary hint passed to the model.</param>
/// <param name="Candidates">
/// With no <paramref name="Language"/>: the languages to pick from, in order of preference. Providers that cannot
/// limit detection detect any language.
/// </param>
public sealed record SttRequest(string? Language, string? Prompt, IReadOnlyList<string>? Candidates = null);

/// <param name="Backend">Provider-specific detail, e.g. the loaded Whisper runtime.</param>
public sealed record Transcript(string Text, string? Language, TimeSpan Elapsed, string? Backend = null);
