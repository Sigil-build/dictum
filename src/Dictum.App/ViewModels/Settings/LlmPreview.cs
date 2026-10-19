using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Processing;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// The LLM editor's live preview: runs the session's copy of the processor on the sample 600 ms after the last change,
/// only while the page is on screen. It writes a pending prompt first, and a newer run always wins.
/// </summary>
public sealed partial class LlmPreview : ObservableObject, IDisposable
{
    /// <summary>Raw dictation as Whisper writes it: no capitals, fillers, a code word.</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>
    {
        ["uk"] = "ну значить я хочу щоб ти перевірив цей код і запушив його в мейн до вечора добре",
        ["en"] = "so um i want you to like check this code and push it to main by the evening okay",
    };

    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(600);
    private static readonly string[] SampleLanguages = ["uk", "en"];

    private readonly SettingsSession _session;
    private readonly IProcessorRunner _runner;
    private readonly ITimers _timers;
    private readonly Func<string?> _selected;
    private readonly Action _flush;
    private bool _shown;
    private IDisposable? _timer;
    private CancellationTokenSource? _run;
    private int _runs;

    /// <param name="selected">The processor the editor shows; null when none.</param>
    /// <param name="flush">Writes a pending prompt edit, so the run uses it.</param>
    public LlmPreview(SettingsSession session, IProcessorRunner runner, ITimers timers, Func<string?> selected, Action flush)
    {
        _session = session;
        _runner = runner;
        _timers = timers;
        _selected = selected;
        _flush = flush;
        Languages = [.. SampleLanguages.Select(code => new Option<string>(code, WhisperLanguages.Name(code)))];
        SampleLanguage = Languages[0];
        SampleText = Samples[SampleLanguages[0]];
    }

    public IReadOnlyList<Option<string>> Languages { get; }

    [ObservableProperty]
    public partial Option<string>? SampleLanguage { get; set; }

    [ObservableProperty]
    public partial string SampleText { get; set; } = "";

    [ObservableProperty]
    public partial string? Text { get; private set; }

    [ObservableProperty]
    public partial string? Latency { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool Running { get; private set; }

    /// <summary>The page is on screen: the selected processor's preview may run.</summary>
    public void Show()
    {
        _shown = true;
        Schedule();
    }

    /// <summary>The page was left: nothing more is sent to the LLM until it shows again.</summary>
    public void Hide()
    {
        _shown = false;
        Cancel();
    }

    /// <summary>Runs the preview after <see cref="Delay"/>, replacing a run scheduled before.</summary>
    public void Schedule()
    {
        if (_selected() is null || !_shown)
        {
            return;
        }

        _timer?.Dispose();
        _timer = _timers.Once(Delay, () => _ = RunAsync());
    }

    /// <summary>Stops a scheduled or running preview; a run still in flight is dropped when it answers.</summary>
    public void Cancel()
    {
        _timer?.Dispose();
        _timer = null;
        _run?.Cancel();
        _runs++;
        Running = false;
    }

    /// <summary>Another processor is shown: the last one's result goes.</summary>
    public void Clear()
    {
        Text = null;
        Latency = null;
        Error = null;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
    }

    partial void OnSampleLanguageChanged(Option<string>? value)
    {
        if (value is not null && Samples.TryGetValue(value.Value, out var sample))
        {
            SampleText = sample;
        }

        Schedule();
    }

    partial void OnSampleTextChanged(string value) => Schedule();

    [RelayCommand]
    private async Task RunAsync()
    {
        _timer?.Dispose();
        _timer = null;
        _flush();
        if (_selected() is not { } id || _session.Config.Processors.GetValueOrDefault(id) is not LlmProcessorConfig llm)
        {
            return;
        }

        _run?.Cancel();
        var cancel = _run = new CancellationTokenSource();
        var run = ++_runs;
        Running = true;
        try
        {
            var context = new ProcessingContext("preview", SampleLanguage?.Value, ActiveApp: null);
            var result = await _runner.RunAsync([(id, llm)], SampleText, context, cancel.Token);
            if (run != _runs)
            {
                return;
            }

            Text = result.Text;
            Latency = UiText.Format(Strings.SettingsLlmLatency, result.Milliseconds);
            Error = result.Error;
        }
        catch (OperationCanceledException)
        {
            // A newer preview, or the page left.
        }
        finally
        {
            if (run == _runs)
            {
                Running = false;
            }
        }
    }
}
