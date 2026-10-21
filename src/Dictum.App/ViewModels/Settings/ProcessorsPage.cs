using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Processing;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the Processors page uses besides the config; fakes in tests.</summary>
public sealed record ProcessorsServices(IProcessorRunner Runner, ISecretStore Secrets, PromptLibrary Prompts, ITimers Timers, ILog Log);

/// <summary>A processor in the list; <paramref name="InNoProfile"/> when no profile's chain runs it, which the list marks.</summary>
public sealed record ProcessorItem(string Id, string Caption, bool InNoProfile)
{
    /// <summary>The item's name for screen readers: the id, and the mark when it shows.</summary>
    public override string ToString() => InNoProfile ? $"{Id}, {Strings.SettingsProcessorsInNoProfile}" : Id;
}

/// <summary>
/// Settings → Processors: the processors, and the editor of the selected one. An LLM prompt opens
/// <see cref="LlmEditor"/>; replace rules open <see cref="RulesEditor"/>. A new processor joins every profile whose chain
/// is empty; one that no chain runs is marked in the list and gets a warning and an "Add to profile…" pick. A processor
/// can be removed once no profile's chain uses it, together with its stored key.
/// </summary>
public sealed partial class ProcessorsPage : SettingsPage, IDisposable
{
    /// <summary>llama-server answers to any model name.</summary>
    private const string DefaultModel = "local";

    private readonly SettingsSession _session;
    private readonly ProcessorsServices _services;
    private string? _id;
    private bool _loading;

    public ProcessorsPage(SettingsSession session, ProcessorsServices services)
    {
        _session = session;
        _services = services;
        Llm = new LlmEditor(session, services);
        Rules = new RulesEditor(session);
        session.Changed += Load;
        Load();
    }

    public override string Title => Strings.SettingsProcessors;

    public override string DocsAnchor => DocsAnchors.Settings.Processors;

    public LlmEditor Llm { get; }

    public RulesEditor Rules { get; }

    [ObservableProperty]
    public partial IReadOnlyList<ProcessorItem> Processors { get; private set; } = [];

    [ObservableProperty]
    public partial ProcessorItem? Selected { get; set; }

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    public partial bool HasProcessor { get; private set; }

    [ObservableProperty]
    public partial bool IsLlm { get; private set; }

    [ObservableProperty]
    public partial bool IsRules { get; private set; }

    /// <summary>"Used by: Work, Home"; null when no profile's chain runs the processor, which <see cref="NoChainWarning"/> says instead.</summary>
    [ObservableProperty]
    public partial string? UsedBy { get; private set; }

    /// <summary>No profile's chain runs the processor, so dictation never uses it; null when one does.</summary>
    [ObservableProperty]
    public partial string? NoChainWarning { get; private set; }

    /// <summary>The profiles the warning offers; picking one appends the processor to its chain.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<string>> AddToChoices { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasAddTo { get; private set; }

    /// <summary>The "Add to profile…" pick; the view clears the dropdown after it.</summary>
    [ObservableProperty]
    public partial Choice<string>? AddTo { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool CanRemove { get; private set; }

    public override void OnShown() => Llm.OnShown();

    public override void OnHidden() => Llm.OnHidden();

    public void Dispose() => Llm.Dispose();

    partial void OnSelectedChanged(ProcessorItem? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        Llm.Flush();
        _id = value.Id;
        Load();
    }

    /// <summary>Appends the processor in no chain to the picked profile's chain.</summary>
    partial void OnAddToChanged(Choice<string>? value)
    {
        if (_loading || value is null || _id is not { } id)
        {
            return;
        }

        _session.Edit(c =>
        {
            if (c.FindProfile(value.Value) is { } profile && !profile.Pipeline.Contains(id))
            {
                profile.Pipeline.Add(id);
            }

            return c;
        });
    }

    [RelayCommand]
    private void AddLlm()
    {
        var id = UniqueIds.For("llm", _session.Config.Processors.ContainsKey);
        var model = _session.Config.Processors.Values.OfType<LlmProcessorConfig>().FirstOrDefault();
        var prompt = NewPrompt(id);
        _id = id;
        _session.Edit(c =>
        {
            c.Processors[id] = new LlmProcessorConfig
            {
                BaseUrl = model?.BaseUrl ?? DefaultConfig.LocalServerUrl,
                Model = model?.Model ?? DefaultModel,
                Prompt = prompt,
                ApiKey = model?.ApiKey,
            };
            return JoinEmptyChains(c, id);
        });
    }

    [RelayCommand]
    private void AddRules()
    {
        var id = UniqueIds.For("rules", _session.Config.Processors.ContainsKey);
        _id = id;
        _session.Edit(c =>
        {
            c.Processors[id] = new ReplaceProcessorConfig();
            return JoinEmptyChains(c, id);
        });
    }

    /// <summary>
    /// A processor in no chain never runs, so a new one joins every profile that has no steps yet, in the same edit.
    /// A profile with steps keeps its chain as it is.
    /// </summary>
    private static DictumConfig JoinEmptyChains(DictumConfig config, string id)
    {
        foreach (var profile in config.Profiles.Where(p => p.Pipeline.Count == 0))
        {
            profile.Pipeline.Add(id);
        }

        return config;
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (_id is not { } id)
        {
            return;
        }

        var key = (_session.Config.Processors.GetValueOrDefault(id) as LlmProcessorConfig)?.ApiKey;
        Llm.Preview.Cancel();
        _id = null;
        _session.Edit(c =>
        {
            c.Processors.Remove(id);
            return c;
        });
        _ = ApiKeys.RemoveAsync(_services.Secrets, SecretNames.Processor(id), key, _session.Config, _services.Log, CancellationToken.None);
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var config = _session.Config;
            CanEdit = _session.CanEdit;
            List<ProcessorItem> items = [.. config.Processors.Select(kv => new ProcessorItem(kv.Key, Caption(kv.Value), !config.Profiles.Any(p => p.Pipeline.Contains(kv.Key))))];
            if (!items.SequenceEqual(Processors))
            {
                Processors = items;
            }

            Selected = Processors.FirstOrDefault(p => p.Id == _id) ?? (Processors.Count > 0 ? Processors[0] : null);
            _id = Selected?.Id;
            HasProcessor = _id is not null;
            var processor = _id is null ? null : config.Processors[_id];
            IsLlm = processor is LlmProcessorConfig;
            IsRules = processor is ReplaceProcessorConfig;
            if (!IsLlm)
            {
                Llm.Release();
            }

            if (_id is not { } id)
            {
                return;
            }

            if (IsLlm)
            {
                Llm.Load(id, config);
            }
            else
            {
                Rules.Load(id, config);
            }

            var users = config.Profiles.Where(p => p.Pipeline.Contains(id)).Select(p => p.Name ?? p.Id).ToList();
            var unused = users.Count == 0;
            UsedBy = unused ? null : UiText.Format(Strings.SettingsProvidersUsedBy, string.Join(", ", users));
            NoChainWarning = unused ? Strings.SettingsProcessorsNoChain : null;
            List<Choice<string>> choices = unused ? [.. config.Profiles.Select(p => new Choice<string>(p.Id, p.Name ?? p.Id))] : [];
            if (!choices.SequenceEqual(AddToChoices))
            {
                AddToChoices = choices;
            }

            HasAddTo = AddToChoices.Count > 0;
            CanRemove = CanEdit && unused;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// The new processor's own prompt, started from cleanup's text, so typing in it never rewrites the prompt other
    /// processors use. The name is free in any letter case, since Windows file names ignore it. If
    /// the file cannot be written, the processor shares cleanup's prompt and the editor says so.
    /// </summary>
    private string NewPrompt(string id)
    {
        var taken = _services.Prompts.Names()
            .Concat(_session.Config.Processors.Values.OfType<LlmProcessorConfig>().Select(l => l.Prompt))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = UniqueIds.For(id, taken.Contains);

        try
        {
            _services.Prompts.Write(name, _services.Prompts.Read(LlmProcessorConfig.CleanupPrompt));
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn($"could not write prompts/{name}.md for the new processor; it shares {LlmProcessorConfig.CleanupPrompt}", ex);
            return LlmProcessorConfig.CleanupPrompt;
        }
    }

    private static string Caption(ProcessorConfig processor) => processor switch
    {
        LlmProcessorConfig llm => UiText.Format(Strings.SettingsProcessorsLlmCaption, llm.Prompt),
        ReplaceProcessorConfig replace => UiText.Format(Strings.SettingsChainRules, replace.Rules.Count),
        _ => Strings.SettingsChainMissing,
    };
}
