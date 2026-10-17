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
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);

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

    [Fact]
    public Task TheLocatorShowsTheRowsAndTheMeter() => fx.Ui(() =>
    {
        Directory.CreateDirectory(_dir);
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var services = new GeneralServices(new FakeChannel(), new FrameHub(TimeProvider.System), new ManualTimers(),
            new FakeAutostart(), "dictum-gui", new FakeSounds(), TimeProvider.System, NullLog.Instance);
        var page = new GeneralPage(session, services);

        var view = new ViewLocator().Build(page);
        var window = new Window { Content = view, Width = 760, Height = 900 };
        window.Show();

        Assert.IsType<GeneralView>(view);
        var labels = window.GetVisualDescendants().OfType<SettingsRow>().Select(r => r.Label).ToList();
        Assert.Equal(
            [Strings.SettingsStartAtLogin, Strings.SettingsMicrophone, Strings.SettingsSounds, Strings.SettingsHistory,
             Strings.SettingsUiLanguage, Strings.SettingsOverlay],
            labels);
        Assert.Single(window.GetVisualDescendants().OfType<LevelMeterBar>());
        Assert.Single(window.GetVisualDescendants().OfType<NumericUpDown>());
        window.Close();
    });
}
