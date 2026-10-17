using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;

namespace Dictum.App.ViewModels.Settings;

public enum SettingsPageKey
{
    General,
    System,
    History,
}

/// <summary>
/// The Settings window: the rail's pages and the page on screen (a sub-page keeps its parent selected in the rail),
/// and a banner when the pages cannot edit or a save failed. Closing hides the page and saves pending edits.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly Dictionary<SettingsPageKey, SettingsPage> _byKey;
    private readonly HashSet<SettingsPage> _followed = [];

    public SettingsViewModel(SettingsSession session, IReadOnlyList<(SettingsPageKey Key, SettingsPage Page)> pages)
    {
        Session = session;
        _byKey = pages.ToDictionary(p => p.Key, p => p.Page);
        Pages = [.. pages.Select(p => p.Page)];
        foreach (var page in Pages)
        {
            Follow(page);
        }

        Version = string.Format(CultureInfo.CurrentCulture, Strings.SettingsVersion, AppVersion());
        Banner = session.Problem ?? session.SaveError;
        session.Changed += () => Banner = session.Problem ?? session.SaveError;
    }

    /// <summary>The config the pages edit; the shell gives it the config watcher's reports.</summary>
    public SettingsSession Session { get; }

    public IReadOnlyList<SettingsPage> Pages { get; }

    public string Version { get; }

    [ObservableProperty]
    public partial SettingsPage? Selected { get; set; }

    [ObservableProperty]
    public partial SettingsPage? Current { get; private set; }

    [ObservableProperty]
    public partial string? Banner { get; private set; }

    public void Open(SettingsPageKey key)
    {
        var page = _byKey[key];
        if (ReferenceEquals(Selected, page))
        {
            Show(page);
        }
        else
        {
            Selected = page;
        }
    }

    /// <summary>The window closed: the page hides (the meter stops) and pending edits are saved.</summary>
    public void Close()
    {
        Show(null);
        Session.Flush();
    }

    public void OnLink(DaemonLink link)
    {
        foreach (var page in _followed.ToList())
        {
            page.OnLink(link);
        }
    }

    partial void OnSelectedChanged(SettingsPage? value) => Show(value);

    private void Show(SettingsPage? page)
    {
        if (ReferenceEquals(page, Current))
        {
            return;
        }

        Current?.OnHidden();
        Current = page;
        page?.OnShown();
    }

    private void Follow(SettingsPage page)
    {
        if (!_followed.Add(page))
        {
            return;
        }

        page.NavigateRequested += child =>
        {
            Follow(child);
            Show(child);
        };
        page.BackRequested += () => Show(Selected);
    }

    private static string AppVersion() =>
        (typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];
}
