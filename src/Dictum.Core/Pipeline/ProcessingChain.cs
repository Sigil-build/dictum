using System.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Core.Text;

namespace Dictum.Core.Pipeline;

/// <summary>Follows a <see cref="ProcessingChain"/> run step by step.</summary>
public interface IProcessingObserver
{
    /// <summary>Before a step runs, with its stage name (<see cref="StageTiming.Process"/>).</summary>
    void Starting(string stage);

    /// <summary>After a step, with its timing and the text it produced.</summary>
    void Finished(StageTiming timing, string text);
}

/// <summary>
/// Runs text through processors in order: each step gets the text of the step before, with invisible characters and
/// outer whitespace removed. Dictation, <c>dictum process</c> and the Settings preview all run this, so they cannot
/// drift apart.
/// </summary>
public static class ProcessingChain
{
    /// <param name="steps">Enumerated as the run goes, so a step that is created lazily and fails stops the run there.</param>
    public static async Task<string> RunAsync(
        IEnumerable<ITextProcessor> steps,
        string text,
        ProcessingContext context,
        IProcessingObserver? observer,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            var stage = StageTiming.Process(step.Id);
            observer?.Starting(stage);
            var watch = Stopwatch.StartNew();
            text = TextChecks.StripInvisible(await step.ProcessAsync(text, context, ct).ConfigureAwait(false)).Trim();
            observer?.Finished(new StageTiming(stage, watch.Elapsed), text);
        }

        return text;
    }
}
