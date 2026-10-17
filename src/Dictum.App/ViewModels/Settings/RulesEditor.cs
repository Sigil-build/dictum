using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Settings;

/// <summary>The replace-rules table of the Processors page; Task 5 fills it.</summary>
public sealed partial class RulesEditor(SettingsSession session) : ObservableObject
{
    private readonly SettingsSession _session = session;

    public void Load(string id, DictumConfig config)
    {
    }
}
