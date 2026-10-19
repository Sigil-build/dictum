using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// General → Pasting:
/// <list type="bullet">
/// <item>the clipboard restore delay;</item>
/// <item>the apps that paste with Ctrl+Shift+V;</item>
/// <item>on Linux, the Wayland paste backend.</item>
/// </list>
/// </summary>
public sealed partial class PastingPage : SettingsPage
{
    public static decimal MaxPasteDelay { get; } = ConfigValidator.MaxPasteDelayMs;

    private readonly SettingsSession _session;
    private bool _loading;
    private bool _delayEmpty;

    public PastingPage(SettingsSession session, string parentTitle, bool linux)
    {
        _session = session;
        BackLabel = UiText.Format(Strings.SettingsBack, parentTitle);
        ShowsInjector = linux;
        Injectors =
        [
            new(InjectorBackend.Auto, Strings.SettingsInjectorAuto),
            new(InjectorBackend.Portal, Strings.SettingsInjectorPortal),
            new(InjectorBackend.Ydotool, "ydotool"),
            new(InjectorBackend.Wtype, "wtype"),
        ];
        session.Changed += Load;
        Load();
    }

    public override string Title => Strings.SettingsPasting;

    public string BackLabel { get; }

    public bool ShowsInjector { get; }

    public IReadOnlyList<Option<InjectorBackend>> Injectors { get; }

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    public partial decimal? PasteDelay { get; set; }

    [ObservableProperty]
    public partial string? PasteDelayError { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<WordChip> TerminalApps { get; private set; } = [];

    [ObservableProperty]
    public partial Option<InjectorBackend>? Injector { get; set; }

    /// <summary>Text from the apps box: complete names are added; the part still being typed comes back, as in <see cref="ProfileWords.Add"/>.</summary>
    public string AddApps(string text)
    {
        var (apps, typing) = WordList.Split(text);
        if (apps.Count == 0)
        {
            return text;
        }

        Edit(g => g with { TerminalApps = WordList.Add(g.TerminalApps, apps) });
        return typing;
    }

    partial void OnPasteDelayChanged(decimal? value)
    {
        if (_loading)
        {
            return;
        }

        if (value is not { } delay)
        {
            _delayEmpty = true;
            var saved = _session.OnDisk.General.PasteDelayMs;
            Edit(g => g with { PasteDelayMs = saved });
            PasteDelayError = Strings.SettingsNumberRequired;
            return;
        }

        _delayEmpty = false;
        Edit(g => g with { PasteDelayMs = (int)decimal.Truncate(delay) });
    }

    partial void OnInjectorChanged(Option<InjectorBackend>? value)
    {
        if (value is not null)
        {
            Edit(g => g with { Injector = value.Value });
        }
    }

    [RelayCommand]
    private void Back() => GoBack();

    private void Edit(Func<GeneralSettings, GeneralSettings> change)
    {
        if (!_loading)
        {
            _session.Edit(c => c with { General = change(c.General) });
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var general = _session.Config.General;
            CanEdit = _session.CanEdit;
            if (!_delayEmpty)
            {
                PasteDelay = general.PasteDelayMs;
                PasteDelayError = _session.IssueAt(ConfigPaths.General(nameof(GeneralSettings.PasteDelayMs)));
            }

            TerminalApps = [.. general.TerminalApps.Select(app => new WordChip(app,
                new RelayCommand(() => Edit(g => g with { TerminalApps = [.. g.TerminalApps.Where(a => a != app)] }))))];
            Injector = Injectors.FirstOrDefault(i => i.Value == general.Injector) ?? Injectors[0];
        }
        finally
        {
            _loading = false;
        }
    }
}
