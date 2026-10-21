using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Processing;

namespace Dictum.Core.Tests.Processing;

public class ReplaceProcessorTests
{
    private static readonly ProcessingContext Context = new("p", "uk", null);

    [Fact]
    public async Task AppliesALiteralAndARegexRule()
    {
        var processor = new ReplaceProcessor("dev-terms", new ReplaceProcessorConfig
        {
            Rules =
            [
                new ReplaceRule { From = "дот нет", To = ".NET" },
                new ReplaceRule { Pattern = @"(?i)\bавалоні[яї]\b", To = "Avalonia" },
            ],
        }, NullLog.Instance);

        var result = await processor.ProcessAsync("Пишу на Дот Нет і Авалонії", Context, TestContext.Current.CancellationToken);

        Assert.Equal("Пишу на .NET і Avalonia", result);
    }

    [Fact]
    public async Task CaseSensitiveLiteralRuleAndRegexGroups()
    {
        var processor = new ReplaceProcessor("r", new ReplaceProcessorConfig
        {
            Rules =
            [
                new ReplaceRule { From = "Api", To = "API", IgnoreCase = false },
                new ReplaceRule { Pattern = @"(\d+) відсотків", To = "$1%" },
            ],
        }, NullLog.Instance);

        var result = await processor.ProcessAsync("api Api 50 відсотків", Context, TestContext.Current.CancellationToken);

        Assert.Equal("api API 50%", result);
    }

    // "Match case" in Settings applies to a regex rule too: off, the rule finds any letter case; on, only the same.
    [Fact]
    public async Task MatchCaseAppliesToRegexRules()
    {
        var processor = new ReplaceProcessor("r", new ReplaceProcessorConfig
        {
            Rules =
            [
                new ReplaceRule { Pattern = @"\bвіжуал студіо\b", To = "Visual Studio" },
                new ReplaceRule { Pattern = @"\bApi\b", To = "API", IgnoreCase = false },
            ],
        }, NullLog.Instance);

        var result = await processor.ProcessAsync("Віжуал Студіо і віжуал студіо, api і Api", Context, TestContext.Current.CancellationToken);

        Assert.Equal("Visual Studio і Visual Studio, api і API", result);
    }

    [Fact]
    public async Task WholeWordLeavesLongerWordsAlone()
    {
        var processor = new ReplaceProcessor("r", new ReplaceProcessorConfig
        {
            Rules =
            [
                new ReplaceRule { From = "мейн", To = "main", WholeWord = true },
                new ReplaceRule { From = "api", To = "API", WholeWord = true, IgnoreCase = false },
            ],
        }, NullLog.Instance);

        var result = await processor.ProcessAsync("Мейн, мейнфрейм і api, Api, rapid", Context, TestContext.Current.CancellationToken);

        Assert.Equal("main, мейнфрейм і API, Api, rapid", result);
    }

    [Fact]
    public async Task ADollarInTheReplacementStaysLiteral()
    {
        var processor = new ReplaceProcessor("r", new ReplaceProcessorConfig
        {
            Rules = [new ReplaceRule { From = "п'ять доларів", To = "$5", WholeWord = true }],
        }, NullLog.Instance);

        Assert.Equal("ціна $5", await processor.ProcessAsync("ціна п'ять доларів", Context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisabledRulesAreSkipped()
    {
        var processor = new ReplaceProcessor("r", new ReplaceProcessorConfig
        {
            Rules =
            [
                new ReplaceRule { From = "кубер", To = "Kubernetes", Disabled = true },
                new ReplaceRule { Pattern = "дот нет", To = ".NET", Disabled = true },
            ],
        }, NullLog.Instance);

        Assert.Equal("кубер і дот нет", await processor.ProcessAsync("кубер і дот нет", Context, TestContext.Current.CancellationToken));
    }
}
