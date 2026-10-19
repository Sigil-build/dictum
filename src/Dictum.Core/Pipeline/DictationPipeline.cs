using System.Diagnostics;
using System.Text.RegularExpressions;
using Dictum.Core.Audio;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Core.Processing;
using Dictum.Core.Stt;

namespace Dictum.Core.Pipeline;

/// <summary>
/// Idle → Recording → Transcribing → Processing → Output → Idle.
/// Any failure returns to Idle; the raw text, if any, still goes to history so dictation is never lost.
/// </summary>
/// <param name="clipFilter">Applied to every clip before transcription, e.g. silence trimming.</param>
public sealed partial class DictationPipeline(IAudioSource audio, HistoryStore? history, ILog log, Func<AudioClip, AudioClip>? clipFilter = null)
{
    /// <summary>A hint shorter than this may well be exactly what was said, so it is never treated as an echo.</summary>
    public const int MinHintEchoWords = 4;

    /// <summary>Fewer consecutive vocabulary terms can be real speech, so they are never treated as an echo.</summary>
    private const int MinEchoTerms = 3;

    /// <summary>Whisper tends to hallucinate on very short clips, so they are skipped.</summary>
    public static readonly TimeSpan MinimumClip = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Phrases Whisper emits for silence or noise (learned from video subtitles). Only a transcript that is
    /// nothing but one of these is dropped; real speech containing them is kept.
    /// </summary>
    private static readonly HashSet<string> Hallucinations = new(StringComparer.Ordinal)
    {
        "дякую за перегляд", "дякуємо за перегляд", "субтитри створено спільнотою",
        "спасибо за просмотр", "продолжение следует", "субтитры сделал dimatorzok", "субтитры создавал dimatorzok",
        "thank you for watching", "thanks for watching", "you",
    };

    private readonly Lock _gate = new();
    private readonly Stopwatch _recording = new();
    private PipelineState _state = PipelineState.Idle;

    public event Action<PipelineState>? StateChanged;

    /// <summary>The clip as recorded (16 kHz mono, before trimming), e.g. to save it for diagnostics.</summary>
    public event Action<AudioClip>? ClipRecorded;

    public PipelineState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public async Task StartRecordingAsync(string? deviceId, CancellationToken ct)
    {
        Transition(PipelineState.Idle, PipelineState.Recording);
        try
        {
            _recording.Restart();
            await audio.StartAsync(deviceId, ct).ConfigureAwait(false);
        }
        catch
        {
            SetState(PipelineState.Idle);
            throw;
        }
    }

    /// <summary>Stops recording and runs the clip through the profile.</summary>
    public async Task<DictationResult> StopAsync(DictationProfile profile, ITextOutput? output, CancellationToken ct)
    {
        Transition(PipelineState.Recording, PipelineState.Transcribing);
        AudioClip clip;
        try
        {
            // Never with the dictation's token: a stop that is skipped because of a cancel leaves the microphone held.
            clip = await audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            SetState(PipelineState.Idle);
            throw;
        }

        _recording.Stop();
        return await RunCoreAsync(clip, profile, output, _recording.Elapsed, ct).ConfigureAwait(false);
    }

    /// <summary>Stops recording and discards the audio.</summary>
    public async Task CancelAsync()
    {
        if (State != PipelineState.Recording)
        {
            return;
        }

        try
        {
            await audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            SetState(PipelineState.Idle);
        }
    }

    /// <summary>Runs an existing clip (e.g. from a file) through the profile.</summary>
    public Task<DictationResult> RunAsync(AudioClip clip, DictationProfile profile, ITextOutput? output, CancellationToken ct)
    {
        Transition(PipelineState.Idle, PipelineState.Transcribing);
        return RunCoreAsync(clip, profile, output, recordElapsed: null, ct);
    }

    private async Task<DictationResult> RunCoreAsync(
        AudioClip clip,
        DictationProfile profile,
        ITextOutput? output,
        TimeSpan? recordElapsed,
        CancellationToken ct)
    {
        var progress = new RunProgress();
        if (recordElapsed is { } recorded)
        {
            progress.Stages.Add(new StageTiming(StageTiming.Record, recorded));
        }

        try
        {
            ClipRecorded?.Invoke(clip);
            if (clipFilter is not null)
            {
                clip = clipFilter(clip);
            }

            if (clip.Duration < MinimumClip)
            {
                log.Info($"Clip of {clip.Duration.TotalMilliseconds:F0} ms is too short; skipped");
                return new DictationResult(profile.Id, profile.Stt.Id, "", "", null, clip.Duration, progress.Stages, Skipped: true);
            }

            var (transcript, rawText) = await TranscribeAsync(clip, profile, progress, ct).ConfigureAwait(false);
            var language = transcript.Language ?? profile.Request.Language;
            var text = await RunProcessorsAsync(rawText, language, profile, progress, ct).ConfigureAwait(false);
            if (output is not null && text.Length > 0)
            {
                await WriteOutputAsync(output, text, progress, ct).ConfigureAwait(false);
            }

            var result = new DictationResult(profile.Id, profile.Stt.Id, rawText, text, language, clip.Duration, progress.Stages, transcript.Backend, Model: transcript.Model);
            if (text.Length > 0)
            {
                history?.Append(ToHistoryEntry(result));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (stage, rawText) = (progress.Stage, progress.RawText);

            // The caller reports the failure; keep the details in the log file only.
            log.Write(LogLevel.Debug, $"Dictation failed at {stage}: {ex.Message}", ex);
            if (!string.IsNullOrEmpty(rawText))
            {
                history?.Append(new HistoryEntry(DateTimeOffset.Now, profile.Id, profile.Stt.Id, rawText, rawText, null, clip.Duration.TotalSeconds, $"{stage}: {ex.Message}"));
            }

            throw new DictationException($"Dictation failed at {stage}: {ex.Message}", stage, rawText, ex);
        }
        finally
        {
            SetState(PipelineState.Idle);
        }
    }

    /// <summary>The transcript, and its text with whitespace collapsed and silence hallucinations or hint echoes dropped.</summary>
    private async Task<(Transcript Transcript, string RawText)> TranscribeAsync(
        AudioClip clip,
        DictationProfile profile,
        RunProgress progress,
        CancellationToken ct)
    {
        SetState(PipelineState.Transcribing);
        var watch = Stopwatch.StartNew();
        var transcript = await profile.Stt.TranscribeAsync(clip, profile.Request, ct).ConfigureAwait(false);
        progress.Stages.Add(new StageTiming(StageTiming.Transcribe, watch.Elapsed));
        progress.RawText = NormalizeWhitespace(transcript.Text);
        progress.RawText = DropNoise(progress.RawText, profile);
        log.Info($"Transcribed {clip.Duration.TotalSeconds:F1} s with {profile.Stt.Id} in {watch.ElapsedMilliseconds} ms: {log.Redact(progress.RawText)}");
        return (transcript, progress.RawText);
    }

    /// <summary>The text, or empty when it is only a typical silence hallucination or an echo of the hint.</summary>
    private string DropNoise(string text, DictationProfile profile)
    {
        if (IsKnownHallucination(text))
        {
            log.Info($"Dropped a typical silence hallucination: {log.Redact(text)}");
            return string.Empty;
        }

        if (IsHintEcho(text, profile.Request.Prompt, profile.Vocabulary))
        {
            log.Info($"Dropped an echo of the vocabulary hint: {log.Redact(text)}");
            return string.Empty;
        }

        return text;
    }

    private async Task<string> RunProcessorsAsync(
        string text,
        string? language,
        DictationProfile profile,
        RunProgress progress,
        CancellationToken ct)
    {
        if (text.Length == 0 || profile.Processors.Count == 0)
        {
            return text;
        }

        SetState(PipelineState.Processing);
        var context = ProcessingContext.ForProfile(profile.Id, language, profile.Vocabulary);
        return await ProcessingChain.RunAsync(profile.Processors, text, context, progress, ct).ConfigureAwait(false);
    }

    private async Task WriteOutputAsync(ITextOutput output, string text, RunProgress progress, CancellationToken ct)
    {
        progress.Stage = StageTiming.Output;
        SetState(PipelineState.Output);
        var watch = Stopwatch.StartNew();
        await output.WriteAsync(text, ct).ConfigureAwait(false);
        progress.Stages.Add(new StageTiming(progress.Stage, watch.Elapsed));
    }

    private static HistoryEntry ToHistoryEntry(DictationResult result) =>
        new(
            DateTimeOffset.Now, result.ProfileId, result.Provider, result.RawText, result.Text, result.Language, result.AudioDuration.TotalSeconds,
            Model: result.Model,
            Backend: result.Backend,
            TranscribeMs: (long?)result.Stages.FirstOrDefault(s => s.Stage == StageTiming.Transcribe)?.Elapsed.TotalMilliseconds);

    /// <summary>
    /// STT segments may be joined with line breaks; pasted into a terminal a line break is Enter, so the raw
    /// transcript is collapsed to single spaces. Processors (e.g. an email template) may still add line breaks.
    /// </summary>
    public static string NormalizeWhitespace(string text) => WhitespaceRun().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    public static bool IsKnownHallucination(string text) => Hallucinations.Contains(Words(text));

    /// <summary>
    /// With a vocabulary hint, Whisper may echo the hint on near-silence. A transcript that is the hint itself (when the
    /// hint has at least <see cref="MinHintEchoWords"/> words), or <see cref="MinEchoTerms"/> or more consecutive
    /// vocabulary terms in list order, is dropped; real speech is never only that.
    /// </summary>
    public static bool IsHintEcho(string text, string? hint, IReadOnlyList<string>? vocabulary)
    {
        var words = Words(text);
        if (words.Length == 0)
        {
            return false;
        }

        if (hint is not null && words == Words(hint) && words.Split(' ').Length >= MinHintEchoWords)
        {
            return true;
        }

        if (vocabulary is not { Count: >= MinEchoTerms })
        {
            return false;
        }

        var terms = vocabulary.Select(Words).ToArray();
        for (var start = 0; start + MinEchoTerms <= terms.Length; start++)
        {
            for (var end = start + MinEchoTerms; end <= terms.Length; end++)
            {
                if (words == string.Join(' ', terms[start..end]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Words(string text) => NonLetters().Replace(text.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonLetters();

    private void Transition(PipelineState from, PipelineState to)
    {
        lock (_gate)
        {
            if (_state != from)
            {
                throw new InvalidOperationException($"Pipeline is {_state}, expected {from}.");
            }

            _state = to;
        }

        StateChanged?.Invoke(to);
    }

    private void SetState(PipelineState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(state);
    }

    /// <summary>How far one run got, so a failure names its stage and keeps the STT text.</summary>
    private sealed class RunProgress : IProcessingObserver
    {
        public string Stage { get; set; } = StageTiming.Transcribe;

        public string? RawText { get; set; }

        public List<StageTiming> Stages { get; } = [];

        public void Starting(string stage) => Stage = stage;

        public void Finished(StageTiming timing, string text) => Stages.Add(timing);
    }
}
