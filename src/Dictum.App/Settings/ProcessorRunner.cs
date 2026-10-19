using System.Diagnostics;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Core.Processing;
using Dictum.Host;

namespace Dictum.App.Settings;

/// <summary>The text after a run, how long the run took, and why it stopped early (null when every step ran).</summary>
public sealed record RunResult(string Text, long Milliseconds, string? Error);

/// <summary>Runs processors in the GUI: the Processors preview and History's re-run. A fake in tests.</summary>
public interface IProcessorRunner
{
    /// <summary>Runs <paramref name="steps"/> in order. Never throws, except when <paramref name="ct"/> is cancelled.</summary>
    Task<RunResult> RunAsync(IReadOnlyList<(string Id, ProcessorConfig Config)> steps, string text, ProcessingContext context, CancellationToken ct);
}

/// <summary>
/// Builds the processors from config copies and runs them as the daemon does, with two differences:
/// <list type="bullet">
/// <item>LLM steps fail instead of passing the text through, so a server that does not answer shows its reason;</item>
/// <item>a failing step ends the run with the text so far and the reason.</item>
/// </list>
/// A prompt file is read again when it changes, so a prompt just edited is the one used.
/// </summary>
public sealed class ProcessorRunner(AppPaths paths, ISecretStore secrets, ILog log) : IProcessorRunner, IDisposable
{
    private readonly ProviderFactory _providers = new(paths, secrets, log);

    public async Task<RunResult> RunAsync(IReadOnlyList<(string Id, ProcessorConfig Config)> steps, string text, ProcessingContext context, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var progress = new Progress(text);
        try
        {
            var result = await ProcessingChain.RunAsync(steps.Select(Create), text, context, progress, ct);
            return new RunResult(result, watch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"Processing run stopped: {ex.Message}");
            return new RunResult(progress.Text, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    public void Dispose() => _providers.Dispose();

    private ITextProcessor Create((string Id, ProcessorConfig Config) step) =>
        _providers.CreateProcessor(step.Id, step.Config is LlmProcessorConfig llm ? llm with { OnError = LlmErrorMode.Fail } : step.Config);

    /// <summary>The text after the last step that finished.</summary>
    private sealed class Progress(string input) : IProcessingObserver
    {
        public string Text { get; private set; } = input;

        public void Starting(string stage)
        {
        }

        public void Finished(StageTiming timing, string text) => Text = text;
    }
}
