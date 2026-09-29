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
