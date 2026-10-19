using Dictum.Cli.Commands;
using Dictum.Core.Configuration;
using Dictum.Setup;

namespace Dictum.Cli.Tests;

/// <summary>What <c>dictum init</c> decides from its options before it downloads anything.</summary>
public sealed class InitCommandTests
{
    private const string Server = "http://192.168.1.10:8080/v1";

    private static readonly ExistingLlmServer Current = new(Server, "qwen3-4b", "secret:dictum-cleanup");

    [Theory]
    [InlineData("auto", true)]
    [InlineData("cpu", true)]
    [InlineData("nvidia", false)]
    public void TheGpuOptionTakesAutoOrCpu(string gpu, bool accepted) =>
        Assert.Equal(accepted, CliApp.BuildRoot().Parse(["init", "--gpu", gpu]).Errors.Count == 0);

    [Fact]
    public async Task ALanguageThatIsNotACodeIsAConfigError()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        var ex = Assert.Throws<ConfigException>(() => InitCommand.CheckLanguage(context, "Ukrainian"));

        Assert.Contains("'Ukrainian'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguagesThatSoundAlikeAreWarnedAbout()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        InitCommand.CheckLanguage(context, "uk, ru");

        Assert.StartsWith("warning: 'uk' and 'ru' sound alike", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALanguageListWithoutLookAlikesPassesQuietly()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        InitCommand.CheckLanguage(context, "uk, en");

        Assert.Empty(console.Error.ToString());
    }

    [Fact]
    public async Task NoneTurnsCleanupOff()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        Assert.Equal((null, null), AskCleanup(context, llm: "none", current: Current));
    }

    [Fact]
    public async Task AServerWithoutAModelIsAConfigError()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        var ex = Assert.Throws<ConfigException>(() => AskCleanup(context, llm: Server));

        Assert.Contains("--llm-model", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameServerKeepsTheCurrentModelAndKey()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        var (server, pending) = AskCleanup(context, llm: Server, current: Current);

        Assert.Equal(Current, server);
        Assert.Null(pending);
    }

    [Fact]
    public async Task TheKeyDoesNotFollowToAnotherServer()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        var (server, _) = AskCleanup(context, llm: "http://192.168.1.20:8080/v1", llmModel: "gemma-3", current: Current);

        Assert.Equal(new ExistingLlmServer("http://192.168.1.20:8080/v1", "gemma-3", null), server);
    }

    [Fact]
    public async Task AKeyReferenceIsKeptAsItIs()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        var (server, pending) = AskCleanup(context, llm: Server, llmModel: "qwen3-4b", llmApiKey: "env:LLM_KEY");

        Assert.Equal("env:LLM_KEY", server!.ApiKey);
        Assert.Null(pending);
    }

    [Fact]
    public async Task WithoutTheOptionAndWithoutQuestionsTheCurrentServerStays()
    {
        using var console = new ConsoleCapture();
        await using var context = Context();

        Assert.Equal((Current, null), AskCleanup(context, current: Current));
    }

    private static CliContext Context() => new(CliApp.BuildRoot().Parse(["init", "--yes"]));

    private static (ExistingLlmServer? Server, PendingSecret? Pending) AskCleanup(
        CliContext context, string? llm = null, string? llmModel = null, string? llmApiKey = null, ExistingLlmServer? current = null) =>
        InitCommand.AskCleanup(context, new ConsolePrompt(context.Error), interactive: false, llm, llmModel, llmApiKey, config: null, current);
}
