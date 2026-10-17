using System.Diagnostics;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Core.Text;
using Dictum.Core.Vocabulary;
using Dictum.Processing;

namespace Dictum.App.Settings;

/// <summary>The text after a run, how long the run took, and why it stopped early (null when every step ran).</summary>
public sealed record RunResult(string Text, long Milliseconds, string? Error);

/// <summary>Runs processors in the GUI: the Processors preview and History's re-run (spec 7). A fake in tests.</summary>
public interface IProcessorRunner
{
    /// <summary>Runs <paramref name="steps"/> in order. Never throws, except when <paramref name="ct"/> is cancelled.</summary>
    Task<RunResult> RunAsync(IReadOnlyList<(string Id, ProcessorConfig Config)> steps, string text, ProcessingContext context, CancellationToken ct);
}

/// <summary>
/// Builds the processors from config copies, as the daemon does (code map §4.2), with three differences:
/// <list type="bullet">
/// <item>each run reads prompt files afresh, so a prompt just edited is the one used;</item>
/// <item>LLM steps fail instead of passing the text through, so a server that does not answer shows its reason;</item>
/// <item>a failing step ends the run with the text so far and the reason.</item>
/// </list>
/// </summary>
public sealed class ProcessorRunner(AppPaths paths, ISecretStore secrets, ILog log) : IProcessorRunner, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        // Each processor applies its own timeout from the config.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static ProcessingContext ContextFor(ProfileConfig profile) =>
        new(profile.Id, profile.Language, ActiveApp: null, VocabularyList.Render(VocabularyList.Normalize(profile.Vocabulary)));

    public async Task<RunResult> RunAsync(IReadOnlyList<(string Id, ProcessorConfig Config)> steps, string text, ProcessingContext context, CancellationToken ct)
    {
        var templates = new PromptTemplates(paths.PromptsDirectory);
        var values = new ConfigValueResolver(secrets);
        var watch = Stopwatch.StartNew();
        var result = text;
        try
        {
            foreach (var (id, config) in steps)
            {
                ITextProcessor processor = config switch
                {
                    ReplaceProcessorConfig replace => new ReplaceProcessor(id, replace, log),
                    LlmProcessorConfig llm => new LlmProcessor(id, llm with { OnError = LlmErrorMode.Fail }, _http, values, templates, log),
                    _ => throw new ConfigException($"Processor '{id}' has a type the GUI cannot run."),
                };
                result = TextChecks.StripInvisible(await processor.ProcessAsync(result, context, ct)).Trim();
            }

            return new RunResult(result, watch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"Processing run stopped: {ex.Message}");
            return new RunResult(result, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
