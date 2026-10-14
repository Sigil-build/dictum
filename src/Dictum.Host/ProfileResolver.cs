using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Core.Processing;
using Dictum.Core.Stt;
using Dictum.Core.Vocabulary;

namespace Dictum.Host;

/// <summary>Command-line overrides on top of a configured profile.</summary>
/// <param name="Model">Whisper model file; creates an ad-hoc local provider and wins over <paramref name="Stt"/>.</param>
/// <param name="Pipeline">Replaces the profile's processor chain; empty list = no processing.</param>
/// <param name="Raw">Skip all processing.</param>
public sealed record ProfileOverrides(
    string? Profile = null,
    string? Stt = null,
    string? Model = null,
    string? Language = null,
    IReadOnlyList<string>? Pipeline = null,
    bool Raw = false);

public static class ProfileResolver
{
    public const string AdHocProfileId = "ad-hoc";

    public static DictationProfile Resolve(
        DictumConfig config,
        ProfileOverrides overrides,
        Func<string, ISpeechToText> getStt,
        Func<string, ITextProcessor> getProcessor,
        Func<string, SttConfig, ISpeechToText> createAdHocStt)
    {
        var profile = overrides.Profile is { } profileId
            ? config.Profiles.FirstOrDefault(p => p.Id == profileId)
                ?? throw new ConfigException($"Unknown profile '{profileId}'. Configured: {Known(config.Profiles.Select(p => p.Id))}.")
            : config.Profiles.FirstOrDefault();

        ISpeechToText stt;
        if (overrides.Model is { } model)
        {
            stt = createAdHocStt($"whisper:{Path.GetFileName(model)}", new WhisperLocalSttConfig { Model = model });
        }
        else if ((overrides.Stt ?? profile?.Stt) is { } sttId)
        {
            stt = getStt(sttId);
        }
        else
        {
            throw new ConfigException("No STT provider: pass --stt <id> or --model <file>, or run 'dictum init'.");
        }

        var language = overrides.Language ?? profile?.Language;

        IReadOnlyList<string> steps = overrides.Raw ? [] : overrides.Pipeline ?? profile?.Pipeline ?? [];
        var vocabulary = VocabularyList.Normalize(profile?.Vocabulary ?? []);
        return new DictationProfile(
            profile?.Id ?? AdHocProfileId,
            stt,
            SttRequest.ForSetting(language, SttPromptBuilder.Build(profile?.SttPrompt, vocabulary).Prompt)
                ?? throw new ConfigException($"'{language}' is not an ISO 639-1 language code, a comma-separated list of codes, or 'auto'."),
            [.. steps.Select(getProcessor)],
            vocabulary);
    }

    private static string Known(IEnumerable<string> ids) =>
        ids.Any() ? string.Join(", ", ids) : "none";
}
