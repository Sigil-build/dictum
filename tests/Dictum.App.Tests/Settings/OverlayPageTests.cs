using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Settings;

public sealed class OverlayPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTimers _timers = new();

    public OverlayPageTests() => Directory.CreateDirectory(_dir);

    private ConfigStore Store => new(Path.Combine(_dir, "config.json"), NullLog.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (OverlayPage Page, SettingsSession Session) Open(OverlaySettings? overlay = null)
    {
        Store.Save(new DictumConfig { General = new GeneralSettings { Overlay = overlay ?? new OverlaySettings() } });
        var session = new SettingsSession(Store, _timers, NullLog.Instance);
        session.Load();
        return (new OverlayPage(session, "General"), session);
    }

    [Fact]
    public void TheRowsShowTheOverlaySettings()
    {
        var (page, _) = Open(new OverlaySettings
        {
            Enabled = false,
            Position = OverlayPosition.Top,
            Size = OverlaySize.Large,
            Opacity = 0.6,
            Palette = OverlayPalette.Ember,
        });

        Assert.False(page.Enabled);
        Assert.Equal(OverlayPosition.Top, page.Position?.Value);
        Assert.Equal(OverlaySize.Large, page.Size?.Value);
        Assert.Equal(60, page.Opacity);
        Assert.Equal("60 %", page.OpacityText);
        Assert.Equal(OverlayPalette.Ember, page.Palette?.Value);
        Assert.Equal("‹ General", page.BackLabel);
        Assert.Equal(Strings.SettingsOverlay, page.Title);
    }

    [Fact]
    public void EachChangeIsSaved()
    {
        var (page, _) = Open();

        page.Enabled = false;
        page.Position = page.Positions.Single(p => p.Value == OverlayPosition.Top);
        page.Size = page.Sizes.Single(s => s.Value == OverlaySize.Small);
        page.Opacity = 45;
        page.Palette = page.Palettes.Single(p => p.Value == OverlayPalette.Mist);
        _timers.Fire();

        var saved = Store.Load().General.Overlay;
        Assert.False(saved.Enabled);
        Assert.Equal(OverlayPosition.Top, saved.Position);
        Assert.Equal(OverlaySize.Small, saved.Size);
        Assert.Equal(0.45, saved.Opacity, 3);
        Assert.Equal(OverlayPalette.Mist, saved.Palette);
    }

    [Fact]
    public void ThePreviewFollowsTheSettings()
    {
        var (page, _) = Open();

        page.Size = page.Sizes.Single(s => s.Value == OverlaySize.Large);
        page.Palette = page.Palettes.Single(p => p.Value == OverlayPalette.Ember);
        page.Opacity = 50;

        Assert.Equal(1.25, page.PreviewZoom);
        Assert.Equal(OverlayPalette.Ember, page.PreviewPalette);
        Assert.Equal(0.5, page.PreviewOpacity, 3);
        Assert.Equal("50 %", page.OpacityText);
    }

    [Fact]
    public void BackLeavesTheSubPage()
    {
        var (page, _) = Open();
        var back = 0;
        page.BackRequested += () => back++;

        page.BackCommand.Execute(null);

        Assert.Equal(1, back);
    }

    [Fact]
    public void AnOutsideEditUpdatesTheRows()
    {
        var (page, session) = Open();

        session.OnFileChanged(new DictumConfig { General = new GeneralSettings { Overlay = new OverlaySettings { Size = OverlaySize.Small } } });

        Assert.Equal(OverlaySize.Small, page.Size?.Value);
        Assert.Equal(0.8, page.PreviewZoom);
    }

    [Fact]
    public void GeneralOpensTheSubPage()
    {
        var (_, session) = Open();
        var services = new GeneralServices(new FakeChannel(), new FrameHub(TimeProvider.System), _timers, null, null,
            new FakeSounds(), TimeProvider.System, NullLog.Instance, IsLinux: false);
        var general = new GeneralPage(session, services);
        SettingsPage? opened = null;
        general.NavigateRequested += page => opened = page;

        general.OpenOverlayCommand.Execute(null);

        Assert.Same(general.Overlay, opened);
        Assert.Equal("‹ " + Strings.SettingsGeneral, general.Overlay.BackLabel);
    }
}
