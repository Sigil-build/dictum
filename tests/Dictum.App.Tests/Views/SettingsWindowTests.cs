using Avalonia.Controls;
using Avalonia.VisualTree;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public sealed class SettingsWindowTests(AvaloniaFixture fx) : IDisposable
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

    private SettingsViewModel Model(out SettingsViewModelTests.Page general, out SettingsViewModelTests.Page system)
    {
        Directory.CreateDirectory(_dir);
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        general = new SettingsViewModelTests.Page("General");
        system = new SettingsViewModelTests.Page("System");
        return new SettingsViewModel(session, [(SettingsPageKey.General, general), (SettingsPageKey.System, system)]);
    }

    [Fact]
    public Task TheRailListsThePagesAndSelectingOneShowsIt() => fx.Ui(() =>
    {
        var model = Model(out _, out var system);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.Open(SettingsPageKey.General);

        var rail = window.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(2, rail.ItemCount);

        rail.SelectedIndex = 1;

        Assert.Same(system, model.Current);
        window.Close();
    });

    [Fact]
    public Task ClosingTheWindowClosesTheModel() => fx.Ui(() =>
    {
        var model = Model(out var general, out _);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.Open(SettingsPageKey.General);

        window.Close();

        Assert.Equal(["shown", "hidden"], general.Calls);
        Assert.DoesNotContain(window, SettingsWindow.Open);
    });

    [Fact]
    public Task TheWindowHasTheDesignSizesAndAnIcon() => fx.Ui(() =>
    {
        var window = new SettingsWindow { DataContext = Model(out _, out _) };

        Assert.Equal((960, 640, 800, 560), (window.Width, window.Height, window.MinWidth, window.MinHeight));
        Assert.NotNull(window.Icon);
    });
}
