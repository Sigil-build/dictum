using Dictum.Core.Configuration;
using Dictum.Setup;

namespace Dictum.App.Tests.Setup;

/// <summary><c>dictum init</c>'s clean-up key, stored where the wizard and Settings look for it.</summary>
public sealed class CleanupKeyTests
{
    private readonly FakeSecrets _secrets = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATypedKeyGoesToTheCleanupProcessorsSecret()
    {
        var (reference, pending) = CleanupKey.KeepOutOfConfig("sk-123");
        await pending!.WriteAsync(_secrets, _ct);

        Assert.Equal("secret:dictum-processor-cleanup", reference);
        Assert.Equal(new Dictionary<string, string> { ["dictum-processor-cleanup"] = "sk-123" }, _secrets.Values);
    }

    [Fact]
    public void WithCleanupTakenByRulesTheKeyGoesToTheLlmProcessorsSecret()
    {
        var current = DefaultConfig.Create();
        current.Processors[SetupPlanner.CleanupId] = new ReplaceProcessorConfig();

        var (reference, pending) = CleanupKey.KeepOutOfConfig("sk-123", current);

        Assert.Equal(("secret:dictum-processor-llm-cleanup", "dictum-processor-llm-cleanup"), (reference, pending!.Name));
    }

    [Theory]
    [InlineData("env:LLM_KEY")]
    [InlineData("secret:shared-key")]
    public void AReferenceIsKeptWithNothingToStore(string given) => Assert.Equal((given, (PendingSecret?)null), CleanupKey.KeepOutOfConfig(given));
}
