using Dictum.Cli.Commands;
using Dictum.Core.Configuration;
using Dictum.Setup;
using Dictum.Testing;

namespace Dictum.Cli.Tests;

/// <summary>What <c>dictum init</c> decides from its options before it downloads anything, and what it removes after the save.</summary>
public sealed class InitCommandTests
{
    private const string Server = "http://192.168.1.10:8080/v1";

    private static readonly ExistingLlmServer Current = new(Server, "qwen3-4b", "secret:dictum-cleanup");
    private static readonly HardwareInfo Hardware = new(null, long.MaxValue);

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

    [Theory]
    [InlineData(Server, "sk-new")]
    [InlineData(Server, "env:LLM_KEY")]
    [InlineData("http://192.168.1.20:8080/v1", null)]
    public async Task AKeyThatReplacesTheOldCleanupEntryDeletesItAfterTheSave(string llm, string? key)
    {
        using var console = new ConsoleCapture();
        using var dir = new TempDirectory();
        await using var context = Context(dir.File("config.json"));
        var current = ConfigWithTheOldCleanupEntry();
        var (reference, pending) = key is null ? (null, null) : CleanupKey.KeepOutOfConfig(key, current);
        var plan = Plan(current, new ExistingLlmServer(llm, "qwen3-4b", reference));
        var secrets = new FakeSecrets();
        secrets.Values[SecretNames.LegacyCleanup] = "sk-old";

        await InitCommand.RunSetupAsync(context, current, plan, Hardware, pending, secrets, TestContext.Current.CancellationToken);

        Assert.Equal(reference, Assert.IsType<LlmProcessorConfig>(context.Store.Load().Processors["cleanup"]).ApiKey);
        Assert.Equal(pending is null ? [] : [pending.Name], secrets.Values.Keys);
    }

    [Fact]
    public async Task CleanupTurnedOffKeepsTheOldEntryTheConfigStillRefersTo()
    {
        using var console = new ConsoleCapture();
        using var dir = new TempDirectory();
        await using var context = Context(dir.File("config.json"));
        var current = ConfigWithTheOldCleanupEntry();
        var secrets = new FakeSecrets();
        secrets.Values[SecretNames.LegacyCleanup] = "sk-old";

        await InitCommand.RunSetupAsync(context, current, Plan(current, null), Hardware, null, secrets, TestContext.Current.CancellationToken);

        Assert.Equal("secret:dictum-cleanup", Assert.IsType<LlmProcessorConfig>(context.Store.Load().Processors["cleanup"]).ApiKey);
        Assert.Equal("sk-old", secrets.Values[SecretNames.LegacyCleanup]);
    }

    private static CliContext Context() => new(CliApp.BuildRoot().Parse(["init", "--yes"]));

    private static CliContext Context(string configFile) => new(CliApp.BuildRoot().Parse(["init", "--yes", "--config", configFile]));

    /// <summary>A config from an earlier init, which kept the clean-up key under the old entry name.</summary>
    private static DictumConfig ConfigWithTheOldCleanupEntry()
    {
        var config = DefaultConfig.Create();
        config.Processors["cleanup"] = new LlmProcessorConfig { BaseUrl = Server, Model = "qwen3-4b", Prompt = "cleanup", ApiKey = "secret:dictum-cleanup" };
        config.Profiles[0] = config.Profiles[0] with { Pipeline = ["cleanup"] };
        return config;
    }

    /// <summary>The plan keeps the current speech model, so it downloads nothing.</summary>
    private static SetupPlan Plan(DictumConfig current, ExistingLlmServer? server) =>
        SetupPlanner.Plan(
            current,
            new SetupAnswers("uk", null, server is null ? CleanupChoice.None : CleanupChoice.ExistingServer, server, []),
            Catalog.BuiltIn,
            _ => true);

    private static (ExistingLlmServer? Server, PendingSecret? Pending) AskCleanup(
        CliContext context, string? llm = null, string? llmModel = null, string? llmApiKey = null, ExistingLlmServer? current = null) =>
        InitCommand.AskCleanup(context, new ConsolePrompt(context.Error), interactive: false, llm, llmModel, llmApiKey, config: null, current);
}
