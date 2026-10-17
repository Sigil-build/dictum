using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Processing;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// The LLM prompt editor of the Processors page (spec 7).
/// <list type="bullet">
/// <item>Connection: URL, model, key.</item>
/// <item>The settings the design leaves out (gate 3): temperature, timeout, on error, wording, max tokens.</item>
/// <item>The prompt: written to <c>prompts/&lt;name&gt;.md</c> 500 ms after the last keystroke.</item>
/// <item>The live preview: runs the session's copy of this processor on the sample 600 ms after the last change, only
/// while the page is on screen. It writes a pending prompt first, and a newer run always wins.</item>
/// </list>
/// </summary>
public sealed partial class LlmEditor : ObservableObject
{
    public static readonly TimeSpan PromptSaveDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>Raw dictation as Whisper writes it: no capitals, fillers, a code word.</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>
    {
        ["uk"] = "ну значить я хочу щоб ти перевірив цей код і запушив його в мейн до вечора добре",
        ["en"] = "so um i want you to like check this code and push it to main by the evening okay",
    };

    private readonly SettingsSession _session;
    private readonly ProcessorsServices _services;
    private string? _id;
    private string? _loadedPrompt;
    private bool _loading;
    private bool _shown;
    private bool _temperatureEmpty;
    private bool _timeoutEmpty;
    private Task? _committing;
    private IDisposable? _promptSave;
    private (string Name, string Text)? _pendingPrompt;
    private IDisposable? _previewTimer;
    private CancellationTokenSource? _preview;
    private int _previewRun;

    public LlmEditor(SettingsSession session, ProcessorsServices services)
    {
        _session = session;
        _services = services;
        ErrorModes = [new(LlmErrorMode.Passthrough, Strings.SettingsLlmOnErrorKeep), new(LlmErrorMode.Fail, Strings.SettingsLlmOnErrorFail)];
        Wordings =
        [
            new((bool?)null, Strings.SettingsLlmWordingAuto),
            new((bool?)true, Strings.SettingsLlmWordingKeep),
            new((bool?)false, Strings.SettingsLlmWordingFree),
        ];
        Languages = [new("uk", WhisperLanguages.Name("uk")), new("en", WhisperLanguages.Name("en"))];
        SampleLanguage = Languages[0];
        SampleText = Samples["uk"];
    }

    public IReadOnlyList<Option<LlmErrorMode>> ErrorModes { get; }

    public IReadOnlyList<Option<bool?>> Wordings { get; }

    public IReadOnlyList<Option<string>> Languages { get; }

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string? UrlError { get; private set; }

    [ObservableProperty]
    public partial string Model { get; set; } = "";

    [ObservableProperty]
    public partial string? ModelError { get; private set; }

    [ObservableProperty]
    public partial string KeyText { get; set; } = "";

    [ObservableProperty]
    public partial string KeyState { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveKeyCommand))]
    public partial bool HasKey { get; private set; }

    [ObservableProperty]
    public partial string? KeyError { get; private set; }

    [ObservableProperty]
    public partial decimal? Temperature { get; set; }

    [ObservableProperty]
    public partial string? TemperatureError { get; private set; }

    [ObservableProperty]
    public partial decimal? TimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial string? TimeoutError { get; private set; }

    [ObservableProperty]
    public partial Option<LlmErrorMode>? OnError { get; set; }

    [ObservableProperty]
    public partial Option<bool?>? Wording { get; set; }

    [ObservableProperty]
    public partial decimal? MaxTokens { get; set; }

    [ObservableProperty]
    public partial string? MaxTokensError { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Prompts { get; private set; } = [];

    [ObservableProperty]
    public partial string? Prompt { get; set; }

    [ObservableProperty]
    public partial string PromptText { get; set; } = "";

    [ObservableProperty]
    public partial string? PromptNote { get; private set; }

    [ObservableProperty]
    public partial string? PromptError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestorePromptCommand))]
    public partial bool CanRestorePrompt { get; private set; }

    [ObservableProperty]
    public partial string NewPromptName { get; set; } = "";

    [ObservableProperty]
    public partial string? NewPromptError { get; private set; }

    [ObservableProperty]
    public partial Option<string>? SampleLanguage { get; set; }

    [ObservableProperty]
    public partial string SampleText { get; set; } = "";

    [ObservableProperty]
    public partial string? PreviewText { get; private set; }

    [ObservableProperty]
    public partial string? PreviewLatency { get; private set; }

    [ObservableProperty]
    public partial string? PreviewError { get; private set; }

    [ObservableProperty]
    public partial bool Previewing { get; private set; }

    /// <summary>Shows processor <paramref name="id"/> from <paramref name="config"/>; on a switch, writes the old one's prompt first.</summary>
    public void Load(string id, DictumConfig config)
    {
        if (config.Processors.GetValueOrDefault(id) is not LlmProcessorConfig llm)
        {
            return;
        }

        var switched = _id != id;
        if (switched)
        {
            Flush();
            CancelPreview();
            _temperatureEmpty = false;
            _timeoutEmpty = false;
            _loadedPrompt = null;
            PreviewText = null;
            PreviewLatency = null;
            PreviewError = null;
        }

        _id = id;
        _loading = true;
        try
        {
            var path = $"processors.{id}";
            Url = llm.BaseUrl;
            UrlError = _session.IssueAt($"{path}.baseUrl");
            Model = llm.Model;
            ModelError = _session.IssueAt($"{path}.model");
            HasKey = llm.ApiKey is not null;
            KeyState = llm.ApiKey switch
            {
                null => Strings.SettingsProvidersNoKey,
                var key when key.StartsWith(ApiKeys.SecretPrefix, StringComparison.Ordinal) => Strings.SettingsProvidersKeyStored,
                var key when key.StartsWith("env:", StringComparison.Ordinal) =>
                    string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersKeyEnv, key["env:".Length..]),
                _ => Strings.SettingsProvidersKeyPlain,
            };
            if (!_temperatureEmpty)
            {
                Temperature = (decimal)llm.Temperature;
                TemperatureError = _session.IssueAt($"{path}.temperature");
            }

            if (!_timeoutEmpty)
            {
                TimeoutSeconds = llm.TimeoutMs / 1000m;
                TimeoutError = _session.IssueAt($"{path}.timeoutMs");
            }

            OnError = ErrorModes.FirstOrDefault(m => m.Value == llm.OnError) ?? ErrorModes[0];
            Wording = Wordings.FirstOrDefault(w => w.Value == llm.PreserveText) ?? Wordings[0];
            MaxTokens = llm.MaxTokens;
            MaxTokensError = _session.IssueAt($"{path}.maxTokens");
            var names = _services.Prompts.Names();
            if (!names.Contains(llm.Prompt))
            {
                names = [.. names, llm.Prompt];
            }

            if (!names.SequenceEqual(Prompts))
            {
                Prompts = names;
            }

            Prompt = llm.Prompt;
            if (_loadedPrompt != llm.Prompt)
            {
                _loadedPrompt = llm.Prompt;
                PromptText = _services.Prompts.Read(llm.Prompt);
            }

            ShowPromptState(llm.Prompt);
        }
        finally
        {
            _loading = false;
        }

        if (switched)
        {
            SchedulePreview();
        }
    }

    /// <summary>Writes a pending prompt edit now: the page is leaving, another processor is chosen, or a preview runs.</summary>
    public void Flush()
    {
        if (_pendingPrompt is null)
        {
            return;
        }

        _promptSave?.Dispose();
        _promptSave = null;
        SavePrompt();
    }

    /// <summary>The page is on screen: the selected processor's preview may run.</summary>
    public void OnShown()
    {
        _shown = true;
        SchedulePreview();
    }

    /// <summary>The page was left: nothing more is sent to the LLM until it shows again (final review I1).</summary>
    public void OnHidden()
    {
        _shown = false;
        Flush();
        CancelPreview();
    }

    /// <summary>
    /// A rules processor, or none, is selected: writes a pending prompt, stops the preview and forgets the processor,
    /// so selecting it again loads it afresh and previews it.
    /// </summary>
    public void Release()
    {
        Flush();
        CancelPreview();
        _id = null;
    }

    /// <summary>Stops a scheduled or running preview; a run still in flight is dropped when it answers.</summary>
    public void CancelPreview()
    {
        _previewTimer?.Dispose();
        _previewTimer = null;
        _preview?.Cancel();
        _previewRun++;
        Previewing = false;
    }

    partial void OnUrlChanged(string value) => EditLlm(l => l with { BaseUrl = value.Trim() });

    partial void OnModelChanged(string value) => EditLlm(l => l with { Model = value.Trim() });

    partial void OnTemperatureChanged(decimal? value)
    {
        if (_loading)
        {
            return;
        }

        if (value is not { } temperature)
        {
            // As General's History field (4a review C1): nothing typed on the way to empty is saved.
            _temperatureEmpty = true;
            var saved = (_session.OnDisk.Processors.GetValueOrDefault(_id ?? "") as LlmProcessorConfig)?.Temperature ?? 0.2;
            EditLlm(l => l with { Temperature = saved });
            TemperatureError = Strings.SettingsNumberRequired;
            return;
        }

        _temperatureEmpty = false;
        EditLlm(l => l with { Temperature = (double)temperature });
    }

    partial void OnTimeoutSecondsChanged(decimal? value)
    {
        if (_loading)
        {
            return;
        }

        if (value is not { } seconds)
        {
            _timeoutEmpty = true;
            var saved = (_session.OnDisk.Processors.GetValueOrDefault(_id ?? "") as LlmProcessorConfig)?.TimeoutMs ?? 4_000;
            EditLlm(l => l with { TimeoutMs = saved });
            TimeoutError = Strings.SettingsNumberRequired;
            return;
        }

        _timeoutEmpty = false;
        EditLlm(l => l with { TimeoutMs = (int)decimal.Truncate(seconds * 1000) });
    }

    partial void OnOnErrorChanged(Option<LlmErrorMode>? value)
    {
        if (value is not null)
        {
            EditLlm(l => l with { OnError = value.Value });
        }
    }

    partial void OnWordingChanged(Option<bool?>? value)
    {
        if (value is not null)
        {
            EditLlm(l => l with { PreserveText = value.Value });
        }
    }

    // Empty means automatic: the processor scales the limit to the input.
    partial void OnMaxTokensChanged(decimal? value) => EditLlm(l => l with { MaxTokens = value is { } tokens ? (int)decimal.Truncate(tokens) : null });

    partial void OnPromptChanged(string? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        Flush();
        EditLlm(l => l with { Prompt = value });
    }

    partial void OnPromptTextChanged(string value)
    {
        if (_loading || Prompt is not { } name)
        {
            return;
        }

        _promptSave?.Dispose();
        _pendingPrompt = (name, value);
        _promptSave = _services.Timers.Once(PromptSaveDelay, SavePrompt);
        SchedulePreview();
    }

    partial void OnSampleLanguageChanged(Option<string>? value)
    {
        if (value is not null && Samples.TryGetValue(value.Value, out var sample))
        {
            SampleText = sample;
        }

        SchedulePreview();
    }

    partial void OnSampleTextChanged(string value) => SchedulePreview();

    [RelayCommand(CanExecute = nameof(CanRestorePrompt))]
    private void RestorePrompt()
    {
        if (Prompt is not { } name)
        {
            return;
        }

        _promptSave?.Dispose();
        _promptSave = null;
        _pendingPrompt = null;
        _services.Prompts.Delete(name);
        _loading = true;
        PromptText = _services.Prompts.Read(name);
        _loading = false;
        ShowPromptState(name);
        SchedulePreview();
    }

    [RelayCommand]
    private void CreatePrompt()
    {
        var name = NewPromptName.Trim();
        if (!PromptFiles.IsValidName(name))
        {
            NewPromptError = Strings.SettingsLlmPromptNameInvalid;
            return;
        }

        if (_services.Prompts.Names().Contains(name))
        {
            NewPromptError = Strings.SettingsLlmPromptNameTaken;
            return;
        }

        // The text being edited becomes the new prompt; the old prompt keeps what it had on disk.
        _promptSave?.Dispose();
        _promptSave = null;
        _pendingPrompt = null;
        try
        {
            _services.Prompts.Write(name, PromptText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NewPromptError = string.Format(CultureInfo.CurrentCulture, Strings.SettingsLlmPromptSaveFailed, ex.Message);
            return;
        }

        NewPromptError = null;
        NewPromptName = "";
        _loadedPrompt = name;
        EditLlm(l => l with { Prompt = name });
    }

    [RelayCommand]
    private Task PreviewNowAsync() => PreviewAsync();

    [RelayCommand]
    private Task CommitKeyAsync()
    {
        if (KeyText.Length == 0 || _id is null)
        {
            return _committing ?? Task.CompletedTask;
        }

        var typed = KeyText;
        KeyText = "";
        _committing = StoreKeyAsync(_id, typed);
        return _committing;
    }

    [RelayCommand(CanExecute = nameof(HasKey))]
    private async Task RemoveKeyAsync()
    {
        if (_id is not { } id)
        {
            return;
        }

        var current = (_session.Config.Processors.GetValueOrDefault(id) as LlmProcessorConfig)?.ApiKey;
        EditLlm(l => l with { ApiKey = null });
        await ApiKeys.RemoveAsync(_services.Secrets, ApiKeys.ProcessorSecretName(id), current, _services.Log, CancellationToken.None);
    }

    private async Task StoreKeyAsync(string id, string typed)
    {
        var result = await ApiKeys.StoreAsync(_services.Secrets, ApiKeys.ProcessorSecretName(id), typed, CancellationToken.None);
        if (result.Error is { } error)
        {
            KeyError = string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersKeyFailed, error);
            return;
        }

        KeyError = null;
        EditLlm(l => l with { ApiKey = result.Value });
    }

    private void SavePrompt()
    {
        _promptSave = null;
        if (_pendingPrompt is not { } pending)
        {
            return;
        }

        _pendingPrompt = null;
        try
        {
            _services.Prompts.Write(pending.Name, pending.Text);
            PromptError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PromptError = string.Format(CultureInfo.CurrentCulture, Strings.SettingsLlmPromptSaveFailed, ex.Message);
        }

        if (pending.Name == Prompt)
        {
            ShowPromptState(pending.Name);
        }
    }

    private void ShowPromptState(string name)
    {
        var prompts = _services.Prompts;
        CanRestorePrompt = prompts.IsBuiltIn(name) && prompts.HasFile(name);
        PromptNote = string.Format(CultureInfo.CurrentCulture,
            !prompts.IsBuiltIn(name) ? Strings.SettingsLlmPromptFile
            : prompts.HasFile(name) ? Strings.SettingsLlmPromptOverride
            : Strings.SettingsLlmPromptBuiltIn,
            name);
    }

    private void SchedulePreview()
    {
        if (_loading || _id is null || !_shown)
        {
            return;
        }

        _previewTimer?.Dispose();
        _previewTimer = _services.Timers.Once(PreviewDelay, () => _ = PreviewAsync());
    }

    private async Task PreviewAsync()
    {
        _previewTimer?.Dispose();
        _previewTimer = null;
        Flush();
        if (_id is not { } id || _session.Config.Processors.GetValueOrDefault(id) is not LlmProcessorConfig llm)
        {
            return;
        }

        _preview?.Cancel();
        var cancel = _preview = new CancellationTokenSource();
        var run = ++_previewRun;
        Previewing = true;
        try
        {
            var context = new ProcessingContext("preview", SampleLanguage?.Value, ActiveApp: null);
            var result = await _services.Runner.RunAsync([(id, llm)], SampleText, context, cancel.Token);
            if (run != _previewRun)
            {
                return;
            }

            PreviewText = result.Text;
            PreviewLatency = string.Format(CultureInfo.CurrentCulture, Strings.SettingsLlmLatency, result.Milliseconds);
            PreviewError = result.Error;
        }
        catch (OperationCanceledException)
        {
            // A newer preview, or the page left.
        }
        finally
        {
            if (run == _previewRun)
            {
                Previewing = false;
            }
        }
    }

    private void EditLlm(Func<LlmProcessorConfig, LlmProcessorConfig> change)
    {
        if (_loading || _id is not { } id)
        {
            return;
        }

        _session.Edit(c =>
        {
            if (c.Processors.GetValueOrDefault(id) is LlmProcessorConfig llm)
            {
                c.Processors[id] = change(llm);
            }

            return c;
        });
        SchedulePreview();
    }
}
