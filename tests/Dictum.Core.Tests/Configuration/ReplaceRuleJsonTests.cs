using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Configuration;

public sealed class ReplaceRuleJsonTests
{
    private static DictumConfig With(ReplaceRule rule) => new() { Processors = { ["terms"] = new ReplaceProcessorConfig { Rules = [rule] } } };

    [Fact]
    public void UnsetFlagsAreNotWritten()
    {
        var json = ConfigStore.Serialize(With(new ReplaceRule { From = "кубер", To = "Kubernetes" }));

        Assert.DoesNotContain("wholeWord", json, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SetFlagsRoundTrip()
    {
        var json = ConfigStore.Serialize(With(new ReplaceRule { From = "мейн", To = "main", WholeWord = true, Disabled = true }));

        var rule = ((ReplaceProcessorConfig)ConfigStore.Parse(json).Processors["terms"]).Rules[0];
        Assert.Contains("\"wholeWord\": true", json, StringComparison.Ordinal);
        Assert.True(rule.WholeWord);
        Assert.True(rule.Disabled);
    }

    [Fact]
    public void OldRulesLoadAsWholeAndOn()
    {
        var config = ConfigStore.Parse("""{ "processors": { "terms": { "type": "replace", "rules": [ { "from": "a", "to": "b" } ] } } }""");

        var rule = ((ReplaceProcessorConfig)config.Processors["terms"]).Rules[0];
        Assert.False(rule.WholeWord);
        Assert.False(rule.Disabled);
    }
}
