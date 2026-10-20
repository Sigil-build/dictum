using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Settings;

/// <summary>The processor chain of the profile being edited: its steps in order, and the processors still free to add.</summary>
public sealed partial class ProfileChain : ObservableObject
{
    private readonly SettingsSession _session;
    private readonly Action<Func<ProfileConfig, ProfileConfig>> _edit;

    /// <param name="edit">Changes the profile being edited.</param>
    public ProfileChain(SettingsSession session, Action<Func<ProfileConfig, ProfileConfig>> edit)
    {
        _session = session;
        _edit = edit;
    }

    [ObservableProperty]
    public partial IReadOnlyList<ChainStep> Steps { get; private set; } = [];

    [ObservableProperty]
    public partial bool Empty { get; private set; }

    /// <summary>The processors not in the chain yet.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<string>> Choices { get; private set; } = [];

    [ObservableProperty]
    public partial Choice<string>? ToAdd { get; set; }

    [ObservableProperty]
    public partial bool CanAdd { get; private set; }

    [ObservableProperty]
    public partial string? Note { get; private set; }

    /// <summary>Moves a step; out-of-range positions change nothing.</summary>
    public void Move(int from, int to) => _edit(p =>
    {
        if (from == to || from < 0 || to < 0 || from >= p.Pipeline.Count || to >= p.Pipeline.Count)
        {
            return p;
        }

        var steps = p.Pipeline.ToList();
        var step = steps[from];
        steps.RemoveAt(from);
        steps.Insert(to, step);
        return p with { Pipeline = steps };
    });

    public void Load(DictumConfig config, ProfileConfig profile, string path)
    {
        Steps = [.. profile.Pipeline.Select((id, i) => new ChainStep(id, Kind(config, id), i + 1,
            new RelayCommand(() => _edit(p => p with { Pipeline = [.. p.Pipeline.Where(s => s != id)] }))))];
        Empty = Steps.Count == 0;
        Choices = [.. config.Processors.Keys.Where(id => !profile.Pipeline.Contains(id)).Order(StringComparer.Ordinal)
            .Select(id => new Choice<string>(id, UiText.Format(Strings.SettingsProfilesStep, id, Kind(config, id))))];
        ToAdd = null;
        CanAdd = Choices.Count > 0;
        Note = _session.IssueAt(ConfigPaths.Field(path, nameof(ProfileConfig.Pipeline)));
    }

    partial void OnToAddChanged(Choice<string>? value)
    {
        if (value is not null)
        {
            _edit(p => p with { Pipeline = [.. p.Pipeline, value.Value] });
        }
    }

    private static string Kind(DictumConfig config, string id) => config.Processors.GetValueOrDefault(id) switch
    {
        LlmProcessorConfig => Strings.SettingsChainLlm,
        ReplaceProcessorConfig replace => UiText.Format(Strings.SettingsChainRules, replace.Rules.Count),
        _ => Strings.SettingsChainMissing,
    };
}
