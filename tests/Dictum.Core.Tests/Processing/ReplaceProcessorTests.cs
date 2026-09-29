using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Processing;

namespace Dictum.Core.Tests.Processing;

public class ReplaceProcessorTests
{
    private static readonly ProcessingContext Context = new("p", "uk", null);

    [Fact]
    public async Task AppliesThePlanExampleRules()
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
}
