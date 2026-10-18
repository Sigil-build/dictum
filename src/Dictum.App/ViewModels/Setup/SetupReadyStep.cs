using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Setup;

/// <summary>A part of the hotkey line: a key in a keycap, or the "+" between keys.</summary>
public sealed record KeyPart(string Text, bool IsKey);

/// <summary>
/// Step 8 (spec 8): the saved profile's hotkey as keycaps and how to use it, the overlay as the user will see it, a box
/// to try dictation, and the tray-pinning row unless the icon is known to be visible (SPEC:97).
/// </summary>
public sealed partial class SetupReadyStep(TrayHint tray) : SetupStep
{
    public override string Name => Strings.SetupStepReady;

    [ObservableProperty]
    public partial IReadOnlyList<KeyPart> Keys { get; private set; } = [];

    [ObservableProperty]
    public partial string HowTo { get; private set; } = "";

    [ObservableProperty]
    public partial OverlayPalette Palette { get; private set; }

    [ObservableProperty]
    public partial string? LoginError { get; private set; }

    [ObservableProperty]
    public partial bool ShowTrayRow { get; private set; }

    [ObservableProperty]
    public partial string TrayText { get; private set; } = "";

    [ObservableProperty]
    public partial bool CanOpenTraySettings { get; private set; }

    /// <param name="config">The config just saved.</param>
    /// <param name="loginError">Why the login item could not be written, or null.</param>
    internal void Load(DictumConfig config, string? loginError)
    {
        var first = config.Profiles[0];
        var parts = new List<KeyPart>();
        foreach (var key in (first.Hotkey ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (parts.Count > 0)
            {
                parts.Add(new KeyPart("+", false));
            }

            parts.Add(new KeyPart(key, true));
        }

        Keys = parts;
        HowTo = first.Mode == HotkeyMode.Toggle ? Strings.SetupReadyToggle : Strings.SetupReadyHold;
        Palette = config.General.Overlay.Palette;
        LoginError = loginError;
        ShowTrayRow = tray.Desktop != TrayDesktop.Other && tray.Hidden() != false;
        TrayText = tray.Desktop == TrayDesktop.Kde ? Strings.SetupTrayKde : Strings.SetupTrayWindows;
        CanOpenTraySettings = tray.OpenSettings is not null;
    }

    [RelayCommand]
    private void OpenTraySettings() => tray.OpenSettings?.Invoke();
}
