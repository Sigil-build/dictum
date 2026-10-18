using Dictum.App.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupCleanupStepTests
{
    private const string Reference = "secret:dictum-processor-cleanup";
    private readonly FakeProbe _probe = new();

    private SetupCleanupStep Server(ExistingLlmServer? current = null)
    {
        var step = new SetupCleanupStep(_probe, current) { UseServer = true };
        step.Model = current?.Model ?? "qwen2.5:7b";
        return step;
    }

    [Fact]
    public void OffNeedsNoTest()
    {
        var step = new SetupCleanupStep(_probe, null);

        Assert.Equal(("Text clean-up", false, true, true), (step.Name, step.UseServer, step.Off, step.CanContinue));
        Assert.Equal(SetupCleanupStep.DefaultUrl, step.Url);
        Assert.Equal((CleanupChoice.None, (ExistingLlmServer?)null, (string?)null), step.Answer(Reference));
    }

    [Fact]
    public async Task AServerNeedsAPassedTest()
    {
        var step = Server();
        Assert.False(step.CanContinue);

        await step.TestCommand.ExecuteAsync(null);

        Assert.Equal(("Reachable · 12 ms", true, true), (step.TestResult, step.TestOk, step.CanContinue));
        Assert.Equal(("http://localhost:8080/v1", "qwen2.5:7b", (string?)null), _probe.Calls.Single());
    }

    [Fact]
    public async Task AFailedTestShowsWhyAndBlocks()
    {
        var step = Server();
        _probe.Answer = new ServerCheck(false, "Could not reach http://localhost:8080/v1: Connection refused");

        await step.TestCommand.ExecuteAsync(null);

        Assert.Equal(("Could not reach http://localhost:8080/v1: Connection refused", false, false), (step.TestResult, step.TestOk, step.CanContinue));
    }

    // Review focus 2.
    [Fact]
    public async Task AnEditAfterTheTestAsksForANewOne()
    {
        var step = Server();
        await step.TestCommand.ExecuteAsync(null);

        step.Model = "llama3.1";

        Assert.Equal(((string?)null, false, false), (step.TestResult, step.TestOk, step.CanContinue));
    }

    // Review focus 2.
    [Fact]
    public async Task AnOlderTestNeverPassesNewerInputs()
    {
        var step = Server();
        var held = new TaskCompletionSource<ServerCheck>();
        _probe.Held.Enqueue(held);

        var running = step.TestCommand.ExecuteAsync(null);
        step.Url = "http://other:8080/v1";
        held.SetResult(new ServerCheck(true, "Reachable · 9 ms"));
        await running;

        Assert.Equal(((string?)null, false, false), (step.TestResult, step.TestOk, step.CanContinue));
    }

    // Review focus 2.
    [Fact]
    public async Task TheCurrentKeyGoesOnlyToTheSameServer()
    {
        var step = Server(new ExistingLlmServer("http://localhost:8080/v1", "qwen2.5:7b", "secret:dictum-cleanup"));
        Assert.Equal("Keeps the API key stored for this server.", step.KeyNote);

        await step.TestCommand.ExecuteAsync(null);
        Assert.Equal("secret:dictum-cleanup", _probe.Calls[^1].Key);

        step.Url = "http://other:8080/v1";
        Assert.Null(step.KeyNote);
        await step.TestCommand.ExecuteAsync(null);
        Assert.Null(_probe.Calls[^1].Key);
    }

    [Fact]
    public void TheAnswerCarriesAReferenceAndHoldsATypedKey()
    {
        var current = new ExistingLlmServer("http://localhost:8080/v1", "qwen2.5:7b", "secret:dictum-cleanup");
        var step = Server(current);

        Assert.Equal((CleanupChoice.ExistingServer, current, (string?)null), step.Answer(Reference));

        step.KeyText = " sk-1 ";
        Assert.Equal((CleanupChoice.ExistingServer, current with { ApiKey = Reference }, "sk-1"), step.Answer(Reference));

        step.KeyText = "env:OPENAI_API_KEY";
        Assert.Equal((CleanupChoice.ExistingServer, current with { ApiKey = "env:OPENAI_API_KEY" }, (string?)null), step.Answer(Reference));
    }

    [Fact]
    public void TheTwoRadiosMirror()
    {
        var step = new SetupCleanupStep(_probe, null);

        step.UseServer = true;
        Assert.False(step.Off);

        step.Off = true;
        Assert.False(step.UseServer);
        Assert.True(step.CanContinue);
    }
}
