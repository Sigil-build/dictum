using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Overlay;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Docs;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// General → Listening overlay: on/off, position, size, opacity and palette, with a live preview.
/// The GUI's overlay re-reads the config when it is saved; reduced motion is the OS setting.
/// </summary>
public sealed partial class OverlayPage : SettingsPage
{
    /// <summary>The slider's range: the validator's opacity range in percent.</summary>
    public static double MinOpacityPercent { get; } = Math.Round(ConfigValidator.MinOverlayOpacity * 100);

    public static double MaxOpacityPercent { get; } = Math.Round(ConfigValidator.MaxOverlayOpacity * 100);

    private readonly SettingsSession _session;
    private bool _loading;

    public OverlayPage(SettingsSession session, string parentTitle)
    {
        _session = session;
        BackLabel = UiText.Format(Strings.SettingsBack, parentTitle);
        Positions = [new(OverlayPosition.Bottom, Strings.SettingsOverlayBottom), new(OverlayPosition.Top, Strings.SettingsOverlayTop)];
        Sizes =
        [
            new(OverlaySize.Small, Strings.SettingsOverlaySmall),
            new(OverlaySize.Medium, Strings.SettingsOverlayMedium),
            new(OverlaySize.Large, Strings.SettingsOverlayLarge),
        ];
        Palettes =
        [
            new(OverlayPalette.Aurora, Strings.SettingsOverlayAurora),
            new(OverlayPalette.Ember, Strings.SettingsOverlayEmber),
            new(OverlayPalette.Mist, Strings.SettingsOverlayMist),
        ];
        session.Changed += Load;
        Load();
    }

    public override string Title => Strings.SettingsOverlay;

    public override string DocsAnchor => DocsAnchors.Settings.Overlay;

    public string BackLabel { get; }

    public IReadOnlyList<Choice<OverlayPosition>> Positions { get; }

    public IReadOnlyList<Choice<OverlaySize>> Sizes { get; }

    public IReadOnlyList<Choice<OverlayPalette>> Palettes { get; }

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial Choice<OverlayPosition>? Position { get; set; }

    [ObservableProperty]
    public partial Choice<OverlaySize>? Size { get; set; }

    /// <summary>Percent, 30–100, in steps of 5 on the slider.</summary>
    [ObservableProperty]
    public partial double Opacity { get; set; }

    [ObservableProperty]
    public partial string OpacityText { get; private set; } = "";

    [ObservableProperty]
    public partial Choice<OverlayPalette>? Palette { get; set; }

    [ObservableProperty]
    public partial OverlayPalette PreviewPalette { get; private set; }

    [ObservableProperty]
    public partial double PreviewZoom { get; private set; }

    [ObservableProperty]
    public partial double PreviewOpacity { get; private set; }

    partial void OnEnabledChanged(bool value) => Edit(o => o with { Enabled = value });

    partial void OnPositionChanged(Choice<OverlayPosition>? value)
    {
        if (value is not null)
        {
            Edit(o => o with { Position = value.Value });
        }
    }

    partial void OnSizeChanged(Choice<OverlaySize>? value)
    {
        if (value is not null)
        {
            Edit(o => o with { Size = value.Value });
        }
    }

    partial void OnOpacityChanged(double value) => Edit(o => o with { Opacity = Math.Round(value) / 100 });

    partial void OnPaletteChanged(Choice<OverlayPalette>? value)
    {
        if (value is not null)
        {
            Edit(o => o with { Palette = value.Value });
        }
    }

    [RelayCommand]
    private void Back() => GoBack();

    private void Edit(Func<OverlaySettings, OverlaySettings> change)
    {
        if (!_loading)
        {
            _session.Edit(c => c with { General = c.General with { Overlay = change(c.General.Overlay) } });
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var overlay = _session.Config.General.Overlay;
            CanEdit = _session.CanEdit;
            Enabled = overlay.Enabled;
            Position = Positions.FirstOrDefault(p => p.Value == overlay.Position) ?? Positions[0];
            Size = Sizes.FirstOrDefault(s => s.Value == overlay.Size) ?? Sizes.First(s => s.Value == OverlaySize.Medium);
            Opacity = Math.Round(overlay.Opacity * 100);
            Palette = Palettes.FirstOrDefault(p => p.Value == overlay.Palette) ?? Palettes[0];
            OpacityText = UiText.Format(Strings.SettingsOverlayOpacityValue, Opacity);
            PreviewPalette = overlay.Palette;
            PreviewZoom = OverlayTokens.SizeScale(overlay.Size);
            PreviewOpacity = Math.Clamp(overlay.Opacity, ConfigValidator.MinOverlayOpacity, ConfigValidator.MaxOverlayOpacity);
        }
        finally
        {
            _loading = false;
        }
    }
}
