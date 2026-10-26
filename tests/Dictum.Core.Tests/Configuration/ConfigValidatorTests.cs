using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Configuration;

public class ConfigValidatorTests
{
    /// <summary>Every processor runs in the first profile, so a test sees only the issues it is about.</summary>
    private static DictumConfig RunningAll(DictumConfig config)
    {
        config.Profiles[0].Pipeline = [.. config.Processors.Keys];
        return config;
    }

    [Fact]
    public void DefaultConfigIsValid() => Assert.Empty(ConfigValidator.Validate(DefaultConfig.Create()));

    [Fact]
    public void ReportsUnknownReferencesAndDuplicates()
    {
        var config = DefaultConfig.Create() with
        {
            Profiles =
            [
                new ProfileConfig { Id = "a", Stt = "local", Pipeline = ["ghost"] },
                new ProfileConfig { Id = "a", Stt = "nope" },
            ],
        };

        var paths = ConfigValidator.Validate(config).Select(i => i.Path).ToList();

        Assert.Equal(["profiles[0].pipeline", "profiles[1].id", "profiles[1].stt"], paths);
    }

    // A processor that no pipeline runs never touches a dictation, and doctor and 'config validate' say so.
    [Fact]
    public void AProcessorNoPipelineRunsIsWarnedAbout()
    {
        var config = DefaultConfig.Create() with
        {
            Processors = new()
            {
                ["terms"] = new ReplaceProcessorConfig(),
                ["llm"] = new LlmProcessorConfig { BaseUrl = "http://localhost:8080/v1", Model = "local", Prompt = "cleanup" },
                ["email"] = new ReplaceProcessorConfig(),
            },
            Profiles =
            [
                new ProfileConfig { Id = "a", Stt = "local", Pipeline = ["terms"] },
                new ProfileConfig { Id = "b", Stt = "local", Pipeline = ["email"] },
            ],
        };

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal(new ConfigIssue(ConfigIssueSeverity.Warning, "processors.llm", "no profile's pipeline runs it, so dictation never uses it"), issue);
    }

    // A config from an earlier version may still say "output": "type"; doctor and 'config validate' say it pastes.
    [Fact]
    public void TypeOutputIsWarnedAboutAndPastes()
    {
        var config = DefaultConfig.Create();
        config.Profiles.Add(new ProfileConfig { Id = "typed", Stt = "local", Output = OutputMode.Type });

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal(
            new ConfigIssue(ConfigIssueSeverity.Warning, "profiles[1].output", "typing is not supported, and the profile pastes instead; use \"paste\" or \"clipboard\""),
            issue);
    }

    [Fact]
    public void RulesNeedExactlyOneOfFromOrPatternAndAValidRegex()
    {
        var config = RunningAll(DefaultConfig.Create() with
        {
            Processors = new()
            {
                ["r"] = new ReplaceProcessorConfig
                {
                    Rules =
                    [
                        new ReplaceRule { To = "x" },
                        new ReplaceRule { From = "a", Pattern = "b" },
                        new ReplaceRule { Pattern = "(" },
                        new ReplaceRule { From = "ok", To = "fine" },
                    ],
                },
            },
        });

        var paths = ConfigValidator.Validate(config).Select(i => i.Path).ToList();

        Assert.Equal(["processors.r.rules[0]", "processors.r.rules[1]", "processors.r.rules[2].pattern"], paths);
    }

    [Fact]
    public void JsonBackspaceInPatternIsFlagged()
    {
        var config = RunningAll(DefaultConfig.Create() with
        {
            Processors = ConfigStore.Parse("""{ "processors": { "r": { "type": "replace", "rules": [ { "pattern": "\bword\b", "to": "x" } ] } } }""").Processors,
        });

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal(ConfigIssueSeverity.Warning, issue.Severity);
        Assert.Contains(@"\\b", issue.Message);
    }

    [Fact]
    public void PlainTextApiKeyIsOnlyAWarning()
    {
        var config = DefaultConfig.Create() with
        {
            Stt = new()
            {
                ["local"] = new OpenAiCompatibleSttConfig { BaseUrl = "https://example.com/v1", Model = "m", ApiKey = "sk-123" },
            },
        };

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal(ConfigIssueSeverity.Warning, issue.Severity);
    }

    [Theory]
    [InlineData("ftp://host/v1")]
    [InlineData("localhost:8080")]
    public void BaseUrlMustBeAbsoluteHttp(string url)
    {
        var config = DefaultConfig.Create() with
        {
            Stt = new() { ["local"] = new OpenAiCompatibleSttConfig { BaseUrl = url, Model = "m" } },
        };

        Assert.Equal("stt.local.baseUrl", Assert.Single(ConfigValidator.Validate(config)).Path);
    }

    [Fact]
    public void EmptyVocabularyEntryIsAnErrorAndADuplicateAWarning()
    {
        var config = DefaultConfig.Create();
        config.Profiles[0] = config.Profiles[0] with { Language = "uk", Vocabulary = ["Notepad++", " ", "notepad++"] };

        var issues = ConfigValidator.Validate(config).Select(i => (i.Severity, i.Path)).ToList();

        Assert.Equal(
            [(ConfigIssueSeverity.Error, "profiles[0].vocabulary[1]"), (ConfigIssueSeverity.Warning, "profiles[0].vocabulary[2]")],
            issues);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void LlmMaxTokensMustBePositive(int maxTokens)
    {
        var config = RunningAll(DefaultConfig.Create() with
        {
            Processors = new()
            {
                ["cleanup"] = new LlmProcessorConfig { BaseUrl = "http://localhost:8080/v1", Model = "m", Prompt = "cleanup", MaxTokens = maxTokens },
            },
        });

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal((ConfigIssueSeverity.Error, "processors.cleanup.maxTokens"), (issue.Severity, issue.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    public void VocabularyWithoutAFixedLanguageIsAWarning(string? language)
    {
        var config = DefaultConfig.Create();
        config.Profiles[0] = config.Profiles[0] with { Language = language, Vocabulary = ["Notepad++"] };

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal((ConfigIssueSeverity.Warning, "profiles[0].vocabulary"), (issue.Severity, issue.Path));
        Assert.Contains("language", issue.Message);
    }

    [Fact]
    public void VocabularyBeyondTheHintBudgetIsAWarningNamingTheDroppedTerms()
    {
        var config = DefaultConfig.Create();
        config.Profiles[0] = config.Profiles[0] with
        {
            Language = "uk",
            Vocabulary = [new string('a', 300), new string('b', 150), "Kubernetes"],
        };

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal((ConfigIssueSeverity.Warning, "profiles[0].vocabulary"), (issue.Severity, issue.Path));
        Assert.Contains("Kubernetes", issue.Message);
        Assert.Contains(new string('b', 150), issue.Message);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("auto", true)]
    [InlineData("uk", true)]
    [InlineData("haw", true)]
    [InlineData("uk, en", true)]
    [InlineData("UK", false)]
    [InlineData("ukrainian", false)]
    [InlineData("uk, auto", false)]
    [InlineData("", false)]
    [InlineData(" uk ", false)]
    [InlineData("uk,", false)]
    public void LanguageCodes(string? language, bool valid) =>
        Assert.Equal(valid, ConfigValidator.IsValidLanguage(language));

    [Theory]
    [InlineData("uk, en", "'remote' takes a single language, so with a list the server gets the first language, 'uk'")]
    [InlineData("en, uk", "'remote' takes a single language, so with a list the server gets the first language, 'en'")]
    public void ALanguageListOnAnOpenAiCompatibleProviderIsAWarningNamingTheLanguageSent(string language, string message)
    {
        var config = DefaultConfig.Create();
        config.Stt["remote"] = new OpenAiCompatibleSttConfig { BaseUrl = "https://api.openai.com/v1", Model = "whisper-1" };
        config.Profiles[0] = config.Profiles[0] with { Stt = "remote", Language = language };

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal((ConfigIssueSeverity.Warning, "profiles[0].language"), (issue.Severity, issue.Path));
        Assert.Equal(message, issue.Message);
    }

    [Fact]
    public void ALanguageListOnWhisperIsValid()
    {
        var config = DefaultConfig.Create();
        config.Profiles[0] = config.Profiles[0] with { Language = "uk, en" };

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void ALanguageListWithLanguagesThatSoundAlikeIsAWarning()
    {
        var config = DefaultConfig.Create();
        config.Profiles[0] = config.Profiles[0] with { Language = "uk, be" };

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal((ConfigIssueSeverity.Warning, "profiles[0].language"), (issue.Severity, issue.Path));
        Assert.Contains("'uk' and 'be'", issue.Message);
    }

    [Theory]
    [InlineData(0.2, null, "general.overlay.opacity")]
    [InlineData(1.0, "de", "general.uiLanguage")]
    public void OverlayOpacityAndUiLanguageAreChecked(double opacity, string? uiLanguage, string path)
    {
        var config = DefaultConfig.Create() with
        {
            General = new GeneralSettings { UiLanguage = uiLanguage, Overlay = new OverlaySettings { Opacity = opacity } },
        };

        Assert.Equal(path, Assert.Single(ConfigValidator.Validate(config)).Path);
    }

    [Fact]
    public void ANegativeHistorySizeIsAnError()
    {
        var config = DefaultConfig.Create() with { General = new GeneralSettings { HistorySize = -1 } };

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal((ConfigIssueSeverity.Error, "general.historySize", "must be zero or positive"), (issue.Severity, issue.Path, issue.Message));
    }

    // A hand-edited size above what Settings offers still loads: an error would stop Dictum from using the config at all.
    [Fact]
    public void AHistorySizeAboveWhatSettingsOffersIsAWarning()
    {
        var config = DefaultConfig.Create() with { General = new GeneralSettings { HistorySize = ConfigValidator.MaxHistorySize + 1 } };

        var issue = Assert.Single(ConfigValidator.Validate(config));

        Assert.Equal(
            (ConfigIssueSeverity.Warning, "general.historySize", "is above 5000, the most Settings offers"),
            (issue.Severity, issue.Path, issue.Message));
    }

    [Fact]
    public void TheLargestHistorySizeSettingsOffersIsValid() =>
        Assert.Empty(ConfigValidator.Validate(DefaultConfig.Create() with { General = new GeneralSettings { HistorySize = ConfigValidator.MaxHistorySize } }));

    [Fact]
    public void ARelativeDownloadsFolderIsAnError()
    {
        var relative = DefaultConfig.Create() with { General = new GeneralSettings { DownloadsDirectory = "models" } };
        var absolute = DefaultConfig.Create() with { General = new GeneralSettings { DownloadsDirectory = Path.GetTempPath() } };

        var issue = Assert.Single(ConfigValidator.Validate(relative));

        Assert.Equal(
            (ConfigIssueSeverity.Error, "general.downloadsDirectory", "must be an absolute path, or left out to use the data folder"),
            (issue.Severity, issue.Path, issue.Message));
        Assert.Empty(ConfigValidator.Validate(absolute));
    }
}
