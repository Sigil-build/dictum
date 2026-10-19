using Avalonia.Controls;
using Avalonia.VisualTree;
using Dictum.App.Controls;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public sealed class GeneralViewTests(AvaloniaFixture fx) : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public Task TheLocatorShowsTheRowsAndTheMeter() => fx.Ui(() =>
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var services = new GeneralServices(new FakeChannel(), new FrameHub(TimeProvider.System), new ManualTimers(),
            new FakeAutostart(), "dictum-gui", new FakeSounds(), TimeProvider.System, NullLog.Instance, IsLinux: false);
        var page = new GeneralPage(session, services);

        var view = new ViewLocator().Build(page);
        var window = new Window { Content = view, Width = 760, Height = 900 };
        window.Show();

        Assert.IsType<GeneralView>(view);
        var labels = window.GetVisualDescendants().OfType<SettingsRow>().Select(r => r.Label).ToList();
        Assert.Equal(
            [Strings.SettingsStartAtLogin, Strings.SettingsMicrophone, Strings.SettingsSounds, Strings.SettingsHistory,
             Strings.SettingsUiLanguage, Strings.SettingsOverlay, Strings.SettingsPasting, Strings.SettingsRunSetup],
            labels);
        Assert.Single(window.GetVisualDescendants().OfType<LevelMeterBar>());
        Assert.Single(window.GetVisualDescendants().OfType<NumericUpDown>());
        window.Close();
    });
}
