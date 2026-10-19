using System.Globalization;
using System.Text.RegularExpressions;
using Dictum.Core.Hotkeys;
using Dictum.Core.Vocabulary;

namespace Dictum.Core.Configuration;

/// <summary>Semantic checks that JSON parsing alone does not cover.</summary>
public static class ConfigValidator
{
    public const int MaxPasteDelayMs = 5000;
    public const double MinOverlayOpacity = 0.3;
    public const double MaxOverlayOpacity = 1.0;

    /// <summary>The OpenAI chat API accepts temperatures from 0 to 2.</summary>
    public const double MaxTemperature = 2;

    /// <summary>The most dictations the History setting offers to keep.</summary>
    public const int MaxHistorySize = 5000;

    public static IReadOnlyList<ConfigIssue> Validate(DictumConfig config)
    {
        var issues = new List<ConfigIssue>();
        ValidateGeneral(issues, config.General);

        foreach (var (id, stt) in config.Stt)
        {
            ValidateStt(issues, ConfigPaths.Stt(id), stt);
        }

        foreach (var (id, processor) in config.Processors)
        {
            ValidateProcessor(issues, ConfigPaths.Processor(id), processor);
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var takenHotkeys = new HashSet<Hotkey>();
        for (var i = 0; i < config.Profiles.Count; i++)
        {
            ValidateProfile(issues, ConfigPaths.Profile(i), config.Profiles[i], config, seenIds, takenHotkeys);
        }

        return issues;
    }

    /// <summary>Null, <c>auto</c>, one language code or a list of codes; see <see cref="LanguageList.Parse"/>.</summary>
    public static bool IsValidLanguage(string? language) => LanguageList.Parse(language) is not null;

    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void ValidateGeneral(List<ConfigIssue> issues, GeneralSettings general)
    {
        if (general.HistorySize < 0)
        {
            Error(issues, ConfigPaths.General(nameof(GeneralSettings.HistorySize)), "must be zero or positive");
        }
        else if (general.HistorySize > MaxHistorySize)
        {
            Warning(issues, ConfigPaths.General(nameof(GeneralSettings.HistorySize)), $"is above {MaxHistorySize}, the most Settings offers");
        }

        if (general.PasteDelayMs is < 0 or > MaxPasteDelayMs)
        {
            Error(issues, ConfigPaths.General(nameof(GeneralSettings.PasteDelayMs)), $"must be between 0 and {MaxPasteDelayMs}");
        }

        if (general.Overlay.Opacity is < MinOverlayOpacity or > MaxOverlayOpacity)
        {
            Error(issues, ConfigPaths.Field(ConfigPaths.General(nameof(GeneralSettings.Overlay)), nameof(OverlaySettings.Opacity)), string.Create(CultureInfo.InvariantCulture, $"must be between {MinOverlayOpacity} and {MaxOverlayOpacity:0.0}"));
        }

        if (general.UiLanguage is not (null or UiLanguages.English or UiLanguages.Ukrainian))
        {
            Error(issues, ConfigPaths.General(nameof(GeneralSettings.UiLanguage)),
                $"'{general.UiLanguage}' is not a UI language; use \"{UiLanguages.English}\" or \"{UiLanguages.Ukrainian}\", or leave it out to follow the system");
        }
    }

    private static void ValidateProfile(
        List<ConfigIssue> issues,
        string path,
        ProfileConfig profile,
        DictumConfig config,
        HashSet<string> seenIds,
        HashSet<Hotkey> takenHotkeys)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            Error(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Id)), "must not be empty");
        }
        else if (!seenIds.Add(profile.Id))
        {
            Error(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Id)), $"duplicate profile id '{profile.Id}'");
        }

        if (!config.Stt.ContainsKey(profile.Stt))
        {
            Error(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Stt)), $"unknown STT provider '{profile.Stt}'");
        }

        foreach (var step in profile.Pipeline.Where(step => !config.Processors.ContainsKey(step)))
        {
            Error(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Pipeline)), $"unknown processor '{step}'");
        }

        ValidateLanguage(issues, path, profile, config.Stt.GetValueOrDefault(profile.Stt));
        ValidateVocabulary(issues, path, profile);

        if (profile.Hotkey is not null)
        {
            if (!Hotkey.TryParse(profile.Hotkey, out var hotkey, out var hotkeyError))
            {
                Error(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Hotkey)), hotkeyError);
            }
            else if (!takenHotkeys.Add(hotkey))
            {
                Error(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Hotkey)), $"{hotkey} is already used by another profile");
            }
        }
    }

    private static void ValidateLanguage(List<ConfigIssue> issues, string path, ProfileConfig profile, SttConfig? stt)
    {
        if (LanguageList.Parse(profile.Language) is not { } languages)
        {
            Error(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Language)), $"'{profile.Language}' is not an ISO 639-1 code, a comma-separated list of codes, or 'auto'");
            return;
        }

        if (languages.Count > 1 && stt is OpenAiCompatibleSttConfig)
        {
            Warning(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Language)), $"'{profile.Stt}' takes a single language, so with a list the server detects any language");
        }

        if (LanguageList.SimilarWarning(languages) is { } similar)
        {
            Warning(issues, ConfigPaths.Field(path, nameof(ProfileConfig.Language)), similar);
        }
    }

    private static void ValidateStt(List<ConfigIssue> issues, string path, SttConfig stt)
    {
        switch (stt)
        {
            case WhisperLocalSttConfig whisper:
                Required(issues, ConfigPaths.Field(path, nameof(WhisperLocalSttConfig.Model)), whisper.Model);
                if (whisper.Threads < 0)
                {
                    Error(issues, ConfigPaths.Field(path, nameof(WhisperLocalSttConfig.Threads)), "must be zero (auto) or positive");
                }

                break;
            case OpenAiCompatibleSttConfig remote:
                ValidateUrl(issues, ConfigPaths.Field(path, nameof(OpenAiCompatibleSttConfig.BaseUrl)), remote.BaseUrl);
                Required(issues, ConfigPaths.Field(path, nameof(OpenAiCompatibleSttConfig.Model)), remote.Model);
                ValidateApiKey(issues, ConfigPaths.Field(path, nameof(OpenAiCompatibleSttConfig.ApiKey)), remote.ApiKey);
                if (remote.TimeoutMs <= 0)
                {
                    Error(issues, ConfigPaths.Field(path, nameof(OpenAiCompatibleSttConfig.TimeoutMs)), "must be positive");
                }

                break;
        }
    }

    private static void ValidateProcessor(List<ConfigIssue> issues, string path, ProcessorConfig processor)
    {
        switch (processor)
        {
            case ReplaceProcessorConfig replace:
                for (var i = 0; i < replace.Rules.Count; i++)
                {
                    ValidateRule(issues, ConfigPaths.Item(ConfigPaths.Field(path, nameof(ReplaceProcessorConfig.Rules)), i), replace.Rules[i]);
                }

                break;
            case LlmProcessorConfig llm:
                ValidateUrl(issues, ConfigPaths.Field(path, nameof(LlmProcessorConfig.BaseUrl)), llm.BaseUrl);
                Required(issues, ConfigPaths.Field(path, nameof(LlmProcessorConfig.Model)), llm.Model);
                Required(issues, ConfigPaths.Field(path, nameof(LlmProcessorConfig.Prompt)), llm.Prompt);
                ValidateApiKey(issues, ConfigPaths.Field(path, nameof(LlmProcessorConfig.ApiKey)), llm.ApiKey);
                if (llm.Temperature is < 0 or > MaxTemperature)
                {
                    Error(issues, ConfigPaths.Field(path, nameof(LlmProcessorConfig.Temperature)), $"must be between 0 and {MaxTemperature}");
                }

                if (llm.TimeoutMs <= 0)
                {
                    Error(issues, ConfigPaths.Field(path, nameof(LlmProcessorConfig.TimeoutMs)), "must be positive");
                }

                if (llm.MaxTokens <= 0)
                {
                    Error(issues, ConfigPaths.Field(path, nameof(LlmProcessorConfig.MaxTokens)), "must be positive");
                }

                break;
        }
    }

    private static void ValidateRule(List<ConfigIssue> issues, string path, ReplaceRule rule)
    {
        var hasFrom = !string.IsNullOrEmpty(rule.From);
        var hasPattern = !string.IsNullOrEmpty(rule.Pattern);
        if (hasFrom == hasPattern)
        {
            Error(issues, path, "set exactly one of 'from' or 'pattern'");
            return;
        }

        if (hasPattern)
        {
            if (rule.Pattern!.Contains('\b', StringComparison.Ordinal))
            {
                Warning(issues, ConfigPaths.Field(path, nameof(ReplaceRule.Pattern)), @"contains a backspace character: in JSON, ""\b"" is a backspace; write ""\\b"" for a regex word boundary");
            }

            try
            {
                _ = new Regex(rule.Pattern!, RegexOptions.None, ReplaceRule.RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                Error(issues, ConfigPaths.Field(path, nameof(ReplaceRule.Pattern)), $"invalid regex: {ex.Message}");
            }
        }
    }

    private static void ValidateVocabulary(List<ConfigIssue> issues, string profilePath, ProfileConfig profile)
    {
        var path = ConfigPaths.Field(profilePath, nameof(ProfileConfig.Vocabulary));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < profile.Vocabulary.Count; i++)
        {
            var term = profile.Vocabulary[i];
            if (string.IsNullOrWhiteSpace(term))
            {
                Error(issues, ConfigPaths.Item(path, i), "must not be empty");
            }
            else if (!seen.Add(term.Trim()))
            {
                Warning(issues, ConfigPaths.Item(path, i), $"'{term.Trim()}' is listed twice");
            }
        }

        if (profile.Vocabulary.Count > 0 && profile.Language is null or LanguageList.Auto)
        {
            Warning(issues, path, "a word list can pull language detection towards its own language; set 'language' for this profile");
        }

        var dropped = SttPromptBuilder.Build(profile.SttPrompt, VocabularyList.Normalize(profile.Vocabulary)).Dropped;
        if (dropped.Count > 0)
        {
            Warning(issues, path, $"the speech hint holds {SttPromptBuilder.MaxLength} characters; left out: {string.Join(", ", dropped)}");
        }
    }

    private static void ValidateUrl(List<ConfigIssue> issues, string path, string? url)
    {
        if (!IsHttpUrl(url))
        {
            Error(issues, path, $"'{url}' is not an absolute http(s) URL");
        }
    }

    private static void ValidateApiKey(List<ConfigIssue> issues, string path, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey) && !ConfigValueResolver.IsReference(apiKey))
        {
            Warning(issues, path, "API key is stored in plain text; use 'env:VAR' or 'secret:name' instead");
        }
    }

    private static void Required(List<ConfigIssue> issues, string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Error(issues, path, "must not be empty");
        }
    }

    private static void Error(List<ConfigIssue> issues, string path, string message) =>
        issues.Add(new ConfigIssue(ConfigIssueSeverity.Error, path, message));

    private static void Warning(List<ConfigIssue> issues, string path, string message) =>
        issues.Add(new ConfigIssue(ConfigIssueSeverity.Warning, path, message));
}
