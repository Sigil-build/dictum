using System.Text.RegularExpressions;
using Dictum.Core.Hotkeys;
using Dictum.Core.Vocabulary;

namespace Dictum.Core.Configuration;

/// <summary>Semantic checks that JSON parsing alone does not cover.</summary>
public static class ConfigValidator
{
    public static IReadOnlyList<ConfigIssue> Validate(DictumConfig config)
    {
        var issues = new List<ConfigIssue>();

        if (config.General.HistorySize < 0)
        {
            Error(issues, "general.historySize", "must be zero or positive");
        }

        if (config.General.PasteDelayMs is < 0 or > 5000)
        {
            Error(issues, "general.pasteDelayMs", "must be between 0 and 5000");
        }

        foreach (var (id, stt) in config.Stt)
        {
            ValidateStt(issues, $"stt.{id}", stt);
        }

        foreach (var (id, processor) in config.Processors)
        {
            ValidateProcessor(issues, $"processors.{id}", processor);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hotkeys = new HashSet<Hotkey>();
        for (var i = 0; i < config.Profiles.Count; i++)
        {
            var profile = config.Profiles[i];
            var path = $"profiles[{i}]";
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                Error(issues, $"{path}.id", "must not be empty");
            }
            else if (!seen.Add(profile.Id))
            {
                Error(issues, $"{path}.id", $"duplicate profile id '{profile.Id}'");
            }

            if (!config.Stt.ContainsKey(profile.Stt))
            {
                Error(issues, $"{path}.stt", $"unknown STT provider '{profile.Stt}'");
            }

            foreach (var step in profile.Pipeline.Where(step => !config.Processors.ContainsKey(step)))
            {
                Error(issues, $"{path}.pipeline", $"unknown processor '{step}'");
            }

            if (!IsValidLanguage(profile.Language))
            {
                Error(issues, $"{path}.language", $"'{profile.Language}' is not an ISO 639-1 code or 'auto'");
            }

            ValidateVocabulary(issues, path, profile);

            if (profile.Hotkey is not null)
            {
                if (!Hotkey.TryParse(profile.Hotkey, out var hotkey, out var hotkeyError))
                {
                    Error(issues, $"{path}.hotkey", hotkeyError);
                }
                else if (!hotkeys.Add(hotkey))
                {
                    Error(issues, $"{path}.hotkey", $"{hotkey} is already used by another profile");
                }
            }
        }

        return issues;
    }

    /// <summary>Null, "auto", or a two/three-letter language code.</summary>
    public static bool IsValidLanguage(string? language) =>
        language is null
        || language == "auto"
        || (language.Length is 2 or 3 && language.All(char.IsAsciiLetterLower));

    private static void ValidateStt(List<ConfigIssue> issues, string path, SttConfig stt)
    {
        switch (stt)
        {
            case WhisperLocalSttConfig whisper:
                Required(issues, $"{path}.model", whisper.Model);
                if (whisper.Threads < 0)
                {
                    Error(issues, $"{path}.threads", "must be zero (auto) or positive");
                }

                break;
            case OpenAiCompatibleSttConfig remote:
                ValidateUrl(issues, $"{path}.baseUrl", remote.BaseUrl);
                Required(issues, $"{path}.model", remote.Model);
                ValidateApiKey(issues, $"{path}.apiKey", remote.ApiKey);
                if (remote.TimeoutMs <= 0)
                {
                    Error(issues, $"{path}.timeoutMs", "must be positive");
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
                    ValidateRule(issues, $"{path}.rules[{i}]", replace.Rules[i]);
                }

                break;
            case LlmProcessorConfig llm:
                ValidateUrl(issues, $"{path}.baseUrl", llm.BaseUrl);
                Required(issues, $"{path}.model", llm.Model);
                Required(issues, $"{path}.prompt", llm.Prompt);
                ValidateApiKey(issues, $"{path}.apiKey", llm.ApiKey);
                if (llm.Temperature is < 0 or > 2)
                {
                    Error(issues, $"{path}.temperature", "must be between 0 and 2");
                }

                if (llm.TimeoutMs <= 0)
                {
                    Error(issues, $"{path}.timeoutMs", "must be positive");
                }

                if (llm.MaxTokens <= 0)
                {
                    Error(issues, $"{path}.maxTokens", "must be positive");
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
                issues.Add(new ConfigIssue(
                    ConfigIssueSeverity.Warning,
                    $"{path}.pattern",
                    @"contains a backspace character: in JSON, ""\b"" is a backspace; write ""\\b"" for a regex word boundary"));
            }

            try
            {
                _ = new Regex(rule.Pattern!, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                Error(issues, $"{path}.pattern", $"invalid regex: {ex.Message}");
            }
        }
    }

    private static void ValidateVocabulary(List<ConfigIssue> issues, string path, ProfileConfig profile)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < profile.Vocabulary.Count; i++)
        {
            var term = profile.Vocabulary[i];
            if (string.IsNullOrWhiteSpace(term))
            {
                Error(issues, $"{path}.vocabulary[{i}]", "must not be empty");
            }
            else if (!seen.Add(term.Trim()))
            {
                Warning(issues, $"{path}.vocabulary[{i}]", $"'{term.Trim()}' is listed twice");
            }
        }

        if (profile.Vocabulary.Count > 0 && profile.Language is null or "auto")
        {
            Warning(issues, $"{path}.vocabulary", "a word list can pull language detection towards its own language; set 'language' for this profile");
        }

        var dropped = SttPromptBuilder.Build(profile.SttPrompt, VocabularyList.Normalize(profile.Vocabulary)).Dropped;
        if (dropped.Count > 0)
        {
            Warning(issues, $"{path}.vocabulary", $"the speech hint holds {SttPromptBuilder.MaxLength} characters; left out: {string.Join(", ", dropped)}");
        }
    }

    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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
            issues.Add(new ConfigIssue(
                ConfigIssueSeverity.Warning,
                path,
                "API key is stored in plain text; use 'env:VAR' or 'secret:name' instead"));
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
