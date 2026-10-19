using Dictum.Core.Configuration;
using Dictum.Core.Vocabulary;

namespace Dictum.Core.Processing;

public interface ITextProcessor
{
    /// <summary>Processor instance id from the config, e.g. "cleanup-uk".</summary>
    string Id { get; }

    Task<string> ProcessAsync(string text, ProcessingContext context, CancellationToken ct);
}

/// <param name="Vocabulary">The profile's word list as one line ("Notepad++, VS Code"), or null.</param>
public sealed record ProcessingContext(string ProfileId, string? Language, string? ActiveApp, string? Vocabulary = null)
{
    /// <summary>The context for a profile's chain; <paramref name="language"/> replaces the profile's own setting.</summary>
    public static ProcessingContext ForProfile(ProfileConfig profile, string? language = null) =>
        ForProfile(profile.Id, language ?? profile.Language, VocabularyList.Normalize(profile.Vocabulary));

    /// <param name="vocabulary">The profile's normalized word list.</param>
    public static ProcessingContext ForProfile(string profileId, string? language, IReadOnlyList<string>? vocabulary) =>
        new(profileId, language, ActiveApp: null, VocabularyList.Render(vocabulary));
}
