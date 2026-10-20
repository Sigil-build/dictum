using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// The LLM processor editor of the Processors page: the connection (URL, model, <see cref="Key"/>), the rest of the
/// processor (temperature, timeout, on error, wording, max tokens), its <see cref="Prompt"/> and the live
/// <see cref="Preview"/>.
/// </summary>
public sealed partial class LlmEditor : ObservableObject, IDisposable
{
    public static decimal MaxTemperature { get; } = (decimal)ConfigValidator.MaxTemperature;

    private readonly SettingsSession _session;
    private string? _id;
    private bool _loading;
    private bool _temperatureEmpty;
    private bool _timeoutEmpty;

    public LlmEditor(SettingsSession session, ProcessorsServices services)
    {
        _session = session;
        ErrorModes = [new(LlmErrorMode.Passthrough, Strings.SettingsLlmOnErrorKeep), new(LlmErrorMode.Fail, Strings.SettingsLlmOnErrorFail)];
        Wordings =
        [
            new((bool?)null, Strings.SettingsLlmWordingAuto),
            new((bool?)true, Strings.SettingsLlmWordingKeep),
            new((bool?)false, Strings.SettingsLlmWordingFree),
        ];
        Key = new ApiKeyField(() => _id is { } id ? KeyTarget(id) : null, () => _session.Config, services.Secrets, services.Log);
        Prompt = new PromptEditor(services.Prompts, services.Timers, name => EditLlm(l => l with { Prompt = name }), SchedulePreview);
        Preview = new LlmPreview(session, services.Runner, services.Timers, () => _id, Prompt.Flush);
    }

    public IReadOnlyList<Choice<LlmErrorMode>> ErrorModes { get; }

    public IReadOnlyList<Choice<bool?>> Wordings { get; }

    public ApiKeyField Key { get; }

    public PromptEditor Prompt { get; }

    public LlmPreview Preview { get; }

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string? UrlError { get; private set; }

    [ObservableProperty]
    public partial string Model { get; set; } = "";

    [ObservableProperty]
    public partial string? ModelError { get; private set; }

    [ObservableProperty]
    public partial decimal? Temperature { get; set; }

    [ObservableProperty]
    public partial string? TemperatureError { get; private set; }

    [ObservableProperty]
    public partial decimal? TimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial string? TimeoutError { get; private set; }

    [ObservableProperty]
    public partial Choice<LlmErrorMode>? OnError { get; set; }

    [ObservableProperty]
    public partial Choice<bool?>? Wording { get; set; }

    [ObservableProperty]
    public partial decimal? MaxTokens { get; set; }

    [ObservableProperty]
    public partial string? MaxTokensError { get; private set; }

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
            Prompt.Flush();
            Preview.Cancel();
            _temperatureEmpty = false;
            _timeoutEmpty = false;
            Prompt.Forget();
            Preview.Clear();
        }

        _id = id;
        _loading = true;
        try
        {
            LoadSettings(llm, ConfigPaths.Processor(id));
            Prompt.Load(llm.Prompt, Sharers(id, llm, config));
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

    /// <summary>Writes a pending prompt edit now: the page is leaving or another processor is chosen.</summary>
    public void Flush() => Prompt.Flush();

    /// <summary>The page is on screen: the selected processor's preview may run.</summary>
    public void OnShown() => Preview.Show();

    /// <summary>The page was left: the prompt is written, and nothing more is sent to the LLM until it shows again.</summary>
    public void OnHidden()
    {
        Prompt.Flush();
        Preview.Hide();
    }

    /// <summary>
    /// A rules processor, or none, is selected: writes a pending prompt, stops the preview and forgets the processor,
    /// so selecting it again loads it afresh and previews it.
    /// </summary>
    public void Release()
    {
        Prompt.Flush();
        Preview.Cancel();
        _id = null;
    }

    public void Dispose() => Preview.Dispose();

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
            // As General's History field: nothing typed on the way to empty is saved.
            _temperatureEmpty = true;
            var saved = (_session.OnDisk.Processors.GetValueOrDefault(_id ?? "") as LlmProcessorConfig)?.Temperature ?? LlmProcessorConfig.DefaultTemperature;
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
            var saved = (_session.OnDisk.Processors.GetValueOrDefault(_id ?? "") as LlmProcessorConfig)?.TimeoutMs ?? LlmProcessorConfig.DefaultTimeoutMs;
            EditLlm(l => l with { TimeoutMs = saved });
            TimeoutError = Strings.SettingsNumberRequired;
            return;
        }

        _timeoutEmpty = false;
        EditLlm(l => l with { TimeoutMs = TimeoutFields.Milliseconds(seconds) });
    }

    partial void OnOnErrorChanged(Choice<LlmErrorMode>? value)
    {
        if (value is not null)
        {
            EditLlm(l => l with { OnError = value.Value });
        }
    }

    partial void OnWordingChanged(Choice<bool?>? value)
    {
        if (value is not null)
        {
            EditLlm(l => l with { PreserveText = value.Value });
        }
    }

    // Empty means automatic: the processor scales the limit to the input.
    partial void OnMaxTokensChanged(decimal? value) => EditLlm(l => l with { MaxTokens = value is { } tokens ? (int)decimal.Truncate(tokens) : null });

    /// <summary>The key field's target for processor <paramref name="id"/>: a late answer from the store still goes there.</summary>
    private KeyTarget KeyTarget(string id) => new(
        SecretNames.Processor(id),
        () => (_session.Config.Processors.GetValueOrDefault(id) as LlmProcessorConfig)?.ApiKey,
        value => EditLlm(id, l => l with { ApiKey = value }));

    private void LoadSettings(LlmProcessorConfig llm, string path)
    {
        Url = llm.BaseUrl;
        UrlError = _session.IssueAt(ConfigPaths.Field(path, nameof(LlmProcessorConfig.BaseUrl)));
        Model = llm.Model;
        ModelError = _session.IssueAt(ConfigPaths.Field(path, nameof(LlmProcessorConfig.Model)));
        Key.Show(llm.ApiKey);
        if (!_temperatureEmpty)
        {
            Temperature = (decimal)llm.Temperature;
            TemperatureError = _session.IssueAt(ConfigPaths.Field(path, nameof(LlmProcessorConfig.Temperature)));
        }

        if (!_timeoutEmpty)
        {
            TimeoutSeconds = TimeoutFields.Seconds(llm.TimeoutMs);
            TimeoutError = _session.IssueAt(ConfigPaths.Field(path, nameof(LlmProcessorConfig.TimeoutMs)));
        }

        OnError = ErrorModes.FirstOrDefault(m => m.Value == llm.OnError) ?? ErrorModes[0];
        Wording = Wordings.FirstOrDefault(w => w.Value == llm.PreserveText) ?? Wordings[0];
        MaxTokens = llm.MaxTokens;
        MaxTokensError = _session.IssueAt(ConfigPaths.Field(path, nameof(LlmProcessorConfig.MaxTokens)));
    }

    /// <summary>The other LLM processors that use <paramref name="llm"/>'s prompt.</summary>
    private static List<string> Sharers(string id, LlmProcessorConfig llm, DictumConfig config) =>
        [.. config.Processors
            .Where(kv => kv.Key != id && kv.Value is LlmProcessorConfig other && other.Prompt == llm.Prompt)
            .Select(kv => kv.Key)];

    private void SchedulePreview()
    {
        if (!_loading)
        {
            Preview.Schedule();
        }
    }

    private void EditLlm(Func<LlmProcessorConfig, LlmProcessorConfig> change)
    {
        if (_id is { } id)
        {
            EditLlm(id, change);
        }
    }

    private void EditLlm(string id, Func<LlmProcessorConfig, LlmProcessorConfig> change)
    {
        if (_loading)
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
