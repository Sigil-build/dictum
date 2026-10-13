using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Configuration;

public class ConfigValidatorTests
{
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

    [Fact]
    public void RulesNeedExactlyOneOfFromOrPatternAndAValidRegex()
    {
        var config = DefaultConfig.Create() with
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
        };

        var paths = ConfigValidator.Validate(config).Select(i => i.Path).ToList();

        Assert.Equal(["processors.r.rules[0]", "processors.r.rules[1]", "processors.r.rules[2].pattern"], paths);
    }

    [Fact]
    public void JsonBackspaceInPatternIsFlagged()
    {
        var config = ConfigStore.Parse("""{ "processors": { "r": { "type": "replace", "rules": [ { "pattern": "\bword\b", "to": "x" } ] } } }""");

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
        var config = DefaultConfig.Create() with
        {
            Processors = new()
            {
                ["cleanup"] = new LlmProcessorConfig { BaseUrl = "http://localhost:8080/v1", Model = "m", Prompt = "cleanup", MaxTokens = maxTokens },
            },
        };

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
    [InlineData("UK", false)]
    [InlineData("ukrainian", false)]
    public void LanguageCodes(string? language, bool valid) =>
        Assert.Equal(valid, ConfigValidator.IsValidLanguage(language));
}
