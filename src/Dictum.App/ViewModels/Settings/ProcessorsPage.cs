using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the Processors page uses besides the config; fakes in tests.</summary>
public sealed record ProcessorsServices(IProcessorRunner Runner, ISecretStore Secrets, PromptFiles Prompts, ITimers Timers, ILog Log);

public sealed record ProcessorItem(string Id, string Caption)
{
    public override string ToString() => Id;
}

/// <summary>
/// Settings → Processors (spec 7): the processors, and the editor of the selected one. An LLM prompt opens
/// <see cref="LlmEditor"/>; replace rules open <see cref="RulesEditor"/>. A processor can be removed once no profile's
/// chain uses it, together with its stored key.
/// </summary>
public sealed partial class ProcessorsPage : SettingsPage
{
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

    [ObservableProperty]
    public partial string UsedBy { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool CanRemove { get; private set; }

    public override void OnShown() => Llm.OnShown();

    public override void OnHidden() => Llm.OnHidden();

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

    [RelayCommand]
    private void AddLlm()
    {
        var id = UniqueId(_session.Config, "llm");
        var model = _session.Config.Processors.Values.OfType<LlmProcessorConfig>().FirstOrDefault();
        var prompt = NewPrompt(id);
        _id = id;
        _session.Edit(c =>
        {
            c.Processors[id] = new LlmProcessorConfig
            {
                BaseUrl = model?.BaseUrl ?? "http://localhost:8080/v1",
                Model = model?.Model ?? "local",
                Prompt = prompt,
                ApiKey = model?.ApiKey,
            };
            return c;
        });
    }

    [RelayCommand]
    private void AddRules()
    {
        var id = UniqueId(_session.Config, "rules");
        _id = id;
        _session.Edit(c =>
        {
            c.Processors[id] = new ReplaceProcessorConfig();
            return c;
        });
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (_id is not { } id)
        {
            return;
        }

        var key = (_session.Config.Processors.GetValueOrDefault(id) as LlmProcessorConfig)?.ApiKey;
        Llm.CancelPreview();
        _id = null;
        _session.Edit(c =>
        {
            c.Processors.Remove(id);
            return c;
        });
        _ = ApiKeys.RemoveAsync(_services.Secrets, ApiKeys.ProcessorSecretName(id), key, _session.Config, _services.Log, CancellationToken.None);
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var config = _session.Config;
            CanEdit = _session.CanEdit;
            List<ProcessorItem> items = [.. config.Processors.Select(kv => new ProcessorItem(kv.Key, Caption(kv.Value)))];
            if (!items.SequenceEqual(Processors))
            {
                Processors = items;
            }

            Selected = Processors.FirstOrDefault(p => p.Id == _id) ?? Processors.FirstOrDefault();
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
            UsedBy = users.Count == 0
                ? Strings.SettingsProcessorsUnused
                : string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersUsedBy, string.Join(", ", users));
            CanRemove = CanEdit && users.Count == 0;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// The new processor's own prompt, started from cleanup's text, so typing in it never rewrites the prompt other
    /// processors use (final review I2). The name is free in any letter case, since Windows file names ignore it. If
    /// the file cannot be written, the processor shares cleanup's prompt and the editor says so.
    /// </summary>
    private string NewPrompt(string id)
    {
        var taken = _services.Prompts.Names()
            .Concat(_session.Config.Processors.Values.OfType<LlmProcessorConfig>().Select(l => l.Prompt))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = id;
        for (var n = 2; taken.Contains(name); n++)
        {
            name = $"{id}-{n}";
        }

        try
        {
            _services.Prompts.Write(name, _services.Prompts.Read("cleanup"));
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn($"could not write prompts/{name}.md for the new processor; it shares cleanup", ex);
            return "cleanup";
        }
    }

    private static string Caption(ProcessorConfig processor) => processor switch
    {
        LlmProcessorConfig llm => string.Format(CultureInfo.CurrentCulture, Strings.SettingsProcessorsLlmCaption, llm.Prompt),
        ReplaceProcessorConfig replace => string.Format(CultureInfo.CurrentCulture, Strings.SettingsChainRules, replace.Rules.Count),
        _ => Strings.SettingsChainMissing,
    };

    private static string UniqueId(DictumConfig config, string stem)
    {
        if (!config.Processors.ContainsKey(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            if (!config.Processors.ContainsKey($"{stem}-{n}"))
            {
                return $"{stem}-{n}";
            }
        }
    }
}
