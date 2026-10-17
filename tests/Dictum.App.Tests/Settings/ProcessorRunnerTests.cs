using Dictum.App.Settings;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;

namespace Dictum.App.Tests.Settings;

public sealed class ProcessorRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-runner", Guid.NewGuid().ToString("N")[..8]);
    private readonly ProcessorRunner _runner;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public ProcessorRunnerTests() =>
        _runner = new ProcessorRunner(new AppPaths(Path.Combine(_dir, "config"), Path.Combine(_dir, "data")), new FakeSecrets(), NullLog.Instance);

    public void Dispose()
    {
        _runner.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly ProcessingContext Context = new("work", "uk", null);

    [Fact]
    public async Task StepsRunInOrder()
    {
        (string, ProcessorConfig)[] steps =
        [
            ("a", new ReplaceProcessorConfig { Rules = [new ReplaceRule { From = "кубер", To = "kubernetes" }] }),
            ("b", new ReplaceProcessorConfig { Rules = [new ReplaceRule { From = "kubernetes", To = "Kubernetes" }] }),
        ];

        var result = await _runner.RunAsync(steps, "  деплой у кубер​ ", Context, _ct);

        Assert.Equal(("деплой у Kubernetes", (string?)null), (result.Text, result.Error));
    }

    // Review focus 2.
    [Fact]
    public async Task AnLlmThatFailsSaysSo()
    {
        var llm = new LlmProcessorConfig
        {
            BaseUrl = "http://127.0.0.1:9/v1",
            Model = "m",
            Prompt = "cleanup",
            TimeoutMs = 2000,
            OnError = LlmErrorMode.Passthrough,
        };

        var result = await _runner.RunAsync([("cleanup", llm)], "текст", Context, _ct);

        Assert.NotNull(result.Error);
        Assert.Equal("текст", result.Text);
    }

    [Fact]
    public void AProfileGivesItsLanguageAndWords()
    {
        var context = ProcessorRunner.ContextFor(new ProfileConfig { Id = "work", Stt = "local", Language = "uk, en", Vocabulary = ["Nexus5", "KDE"] });

        Assert.Equal(("work", "uk, en", (string?)null, "Nexus5, KDE"), (context.ProfileId, context.Language, context.ActiveApp, context.Vocabulary));
    }

    [Fact]
    public void ProcessorKeysAreNamedAfterTheProcessor() => Assert.Equal("dictum-processor-cleanup", ApiKeys.ProcessorSecretName("cleanup"));
}
