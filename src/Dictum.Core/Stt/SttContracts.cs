using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Core.Stt;

/// <summary>A speech-to-text provider. Every provider checks itself, for <c>dictum doctor</c> and the "Check" button.</summary>
public interface ISpeechToText : IHealthCheck
{
    /// <summary>Provider instance id from the config, e.g. "local-turbo".</summary>
    string Id { get; }

    Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct);
}

/// <param name="Language">ISO 639-1 code, or null to auto-detect.</param>
/// <param name="Prompt">Vocabulary hint passed to the model.</param>
/// <param name="Candidates">
/// With no <paramref name="Language"/>: the languages to pick from, the first with priority. Providers that take a
/// single language get the first.
/// </param>
public sealed record SttRequest(string? Language, string? Prompt, IReadOnlyList<string>? Candidates = null)
{
    /// <summary>The request for a profile's language setting (see <see cref="LanguageList"/>); null when it is not valid.</summary>
    public static SttRequest? ForSetting(string? language, string? prompt) => LanguageList.Parse(language) switch
    {
        null => null,
        [] => new(language is null ? null : LanguageList.Auto, prompt),
        [var single] => new(single, prompt),
        var codes => new(null, prompt, codes),
    };
}

/// <param name="Backend">Provider-specific detail, e.g. the loaded Whisper runtime.</param>
/// <param name="Model">The model that produced the text: a Whisper file name or a server's model name.</param>
public sealed record Transcript(string Text, string? Language, TimeSpan Elapsed, string? Backend = null, string? Model = null);
