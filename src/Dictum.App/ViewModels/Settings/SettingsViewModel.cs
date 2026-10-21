using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Shell;
using Dictum.Core.Docs;

namespace Dictum.App.ViewModels.Settings;

public enum SettingsPageKey
{
    General,
    Profiles,
    SpeechProviders,
    Processors,
    Models,
    System,
    History,
}

/// <summary>
/// The Settings window: the rail's pages and the page on screen (a sub-page keeps its parent selected in the rail),
/// and a banner when the pages cannot edit or a save failed. Help, beside the version, opens the shown page's section
/// of the Settings reference. Closing hides the page and saves pending edits; disposing ends the pages' timers and runs.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    /// <summary>The informational version ends in "+commit"; the window shows the version before it.</summary>
    private const char BuildMetadataSeparator = '+';

    private readonly Dictionary<SettingsPageKey, SettingsPage> _byKey;
    private readonly HashSet<SettingsPage> _followed = [];
    private readonly OpenDocs _openDocs;

    public SettingsViewModel(SettingsSession session, IReadOnlyList<(SettingsPageKey Key, SettingsPage Page)> pages, OpenDocs openDocs)
    {
        Session = session;
        _openDocs = openDocs;
        _byKey = pages.ToDictionary(p => p.Key, p => p.Page);
        Pages = [.. pages.Select(p => p.Page)];
        foreach (var page in Pages)
        {
            Follow(page);
        }

        Version = UiText.Format(Strings.SettingsVersion, AppVersion());
        Banner = BannerFor(session);
        session.Changed += () => Banner = BannerFor(session);
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

    /// <summary>The window is gone: the pages that own a timer or a run in flight end them.</summary>
    public void Dispose()
    {
        foreach (var page in Pages.OfType<IDisposable>())
        {
            page.Dispose();
        }
    }

    public void OnLink(DaemonLink link)
    {
        foreach (var page in _followed.ToList())
        {
            page.OnLink(link);
        }
    }

    /// <summary>The page on screen, a sub-page included, at its section; the top of the reference when none is shown.</summary>
    [RelayCommand]
    private void Help() => _openDocs(DocsPage.Settings, Current?.DocsAnchor);

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

    private static string? BannerFor(SettingsSession session) =>
        session.Problem ?? session.SaveError ?? (session.HasErrors ? Strings.SettingsNotSaved : null);

    private static string AppVersion() =>
        (typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split(BuildMetadataSeparator)[0];
}
