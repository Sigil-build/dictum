using Dictum.Core.Processing;
using Dictum.Core.Stt;

namespace Dictum.Core.Pipeline;

public enum PipelineState
{
    Idle,
    Recording,
    Transcribing,
    Processing,
    Output,
}

/// <summary>A profile with its providers already created.</summary>
/// <param name="Vocabulary">The profile's normalized word list, for the hint echo guard.</param>
public sealed record DictationProfile(
    string Id,
    ISpeechToText Stt,
    SttRequest Request,
    IReadOnlyList<ITextProcessor> Processors,
    IReadOnlyList<string>? Vocabulary = null);

/// <param name="Stage"><c>record</c>, <c>transcribe</c>, <c>process:&lt;id&gt;</c> or <c>output</c>.</param>
public sealed record StageTiming(string Stage, TimeSpan Elapsed);

/// <param name="Skipped">True when the clip was too short to transcribe.</param>
public sealed record DictationResult(
    string ProfileId,
    string Provider,
    string RawText,
    string Text,
    string? Language,
    TimeSpan AudioDuration,
    IReadOnlyList<StageTiming> Stages,
    string? Backend = null,
    bool Skipped = false,
    string? Model = null);

/// <summary>Where the final text goes: stdout, the clipboard or a simulated paste.</summary>
public interface ITextOutput
{
    Task WriteAsync(string text, CancellationToken ct);
}

/// <summary>A pipeline step failed. <see cref="RawText"/> holds the STT text when it got that far.</summary>
public sealed class DictationException(string message, string stage, string? rawText, Exception inner)
    : Exception(message, inner)
{
    public string Stage { get; } = stage;

    public string? RawText { get; } = rawText;
}
