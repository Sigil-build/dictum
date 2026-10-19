using System.Diagnostics;
using System.Text.RegularExpressions;
using Dictum.Core.Audio;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Core.Processing;
using Dictum.Core.Text;
using Dictum.Core.Vocabulary;

namespace Dictum.Core.Pipeline;

/// <summary>
/// Idle → Recording → Transcribing → Processing → Output → Idle.
/// Any failure returns to Idle; the raw text, if any, still goes to history so dictation is never lost.
/// </summary>
/// <param name="clipFilter">Applied to every clip before transcription, e.g. silence trimming.</param>
public sealed partial class DictationPipeline(IAudioSource audio, HistoryStore? history, ILog log, Func<AudioClip, AudioClip>? clipFilter = null)
{
    /// <summary>Whisper tends to hallucinate on very short clips, so they are skipped.</summary>
    public static readonly TimeSpan MinimumClip = TimeSpan.FromMilliseconds(500);

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
        var stages = new List<StageTiming>();
        if (recordElapsed is { } recorded)
        {
            stages.Add(new StageTiming(StageTiming.Record, recorded));
        }

        string? rawText = null;
        var stage = StageTiming.Transcribe;
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
                return new DictationResult(profile.Id, profile.Stt.Id, "", "", null, clip.Duration, stages, Skipped: true);
            }

            SetState(PipelineState.Transcribing);
            var watch = Stopwatch.StartNew();
            var transcript = await profile.Stt.TranscribeAsync(clip, profile.Request, ct).ConfigureAwait(false);
            stages.Add(new StageTiming(stage, watch.Elapsed));
            rawText = NormalizeWhitespace(transcript.Text);
            if (IsKnownHallucination(rawText))
            {
                log.Info($"Dropped a typical silence hallucination: {log.Redact(rawText)}");
                rawText = string.Empty;
            }
            else if (IsHintEcho(rawText, profile.Request.Prompt, profile.Vocabulary))
            {
                log.Info($"Dropped an echo of the vocabulary hint: {log.Redact(rawText)}");
                rawText = string.Empty;
            }
            log.Info($"Transcribed {clip.Duration.TotalSeconds:F1} s with {profile.Stt.Id} in {watch.ElapsedMilliseconds} ms: {log.Redact(rawText)}");

            var text = rawText;
            var language = transcript.Language ?? profile.Request.Language;
            if (text.Length > 0 && profile.Processors.Count > 0)
            {
                SetState(PipelineState.Processing);
                var context = new ProcessingContext(profile.Id, language, ActiveApp: null, VocabularyList.Render(profile.Vocabulary));
                foreach (var processor in profile.Processors)
                {
                    stage = StageTiming.Process(processor.Id);
                    watch.Restart();
                    text = TextChecks.StripInvisible(await processor.ProcessAsync(text, context, ct).ConfigureAwait(false)).Trim();
                    stages.Add(new StageTiming(stage, watch.Elapsed));
                }
            }

            if (output is not null && text.Length > 0)
            {
                stage = StageTiming.Output;
                SetState(PipelineState.Output);
                watch.Restart();
                await output.WriteAsync(text, ct).ConfigureAwait(false);
                stages.Add(new StageTiming(stage, watch.Elapsed));
            }

            var result = new DictationResult(profile.Id, profile.Stt.Id, rawText, text, language, clip.Duration, stages, transcript.Backend, Model: transcript.Model);
            if (text.Length > 0)
            {
                history?.Append(new HistoryEntry(
                    DateTimeOffset.Now, profile.Id, profile.Stt.Id, rawText, text, language, clip.Duration.TotalSeconds,
                    Model: transcript.Model,
                    Backend: transcript.Backend,
                    TranscribeMs: (long?)stages.FirstOrDefault(s => s.Stage == StageTiming.Transcribe)?.Elapsed.TotalMilliseconds));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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

    /// <summary>
    /// STT segments may be joined with line breaks; pasted into a terminal a line break is Enter, so the raw
    /// transcript is collapsed to single spaces. Processors (e.g. an email template) may still add line breaks.
    /// </summary>
    public static string NormalizeWhitespace(string text) => WhitespaceRun().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

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

    public static bool IsKnownHallucination(string text) =>
        Hallucinations.Contains(NonLetters().Replace(text.ToLowerInvariant(), " ").Trim());

    /// <summary>A hint shorter than this may well be exactly what was said, so it is never treated as an echo.</summary>
    public const int MinHintEchoWords = 4;

    /// <summary>Fewer consecutive vocabulary terms can be real speech, so they are never treated as an echo.</summary>
    private const int MinEchoTerms = 3;

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
}
