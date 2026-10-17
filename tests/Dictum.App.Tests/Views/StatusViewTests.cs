using Avalonia.Controls;
using Avalonia.VisualTree;
using Dictum.App.Daemon;
using Dictum.App.ViewModels;
using Dictum.App.Views;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Views;

public class StatusViewTests(AvaloniaFixture fx)
{
    [Fact]
    public Task TheWindowShowsTheLinkThroughTheViewLocator() => fx.Ui(() =>
    {
        var model = new StatusViewModel("config.json", "dictum-gui.log", () => { });
        model.OnLink(new DaemonLink(DaemonLinkState.Failed, Problem: "The daemon keeps stopping; details are in dictum-gui.log."));
        var window = new Window { Content = model };

        window.Show();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        var restart = window.GetVisualDescendants().OfType<Button>().Single();

        Assert.Contains("The daemon is not running", texts);
        Assert.Contains("The daemon keeps stopping; details are in dictum-gui.log.", texts);
        Assert.True(restart.IsVisible);
        window.Close();
    });

    [Fact]
    public Task TheLocatorBuildsViewsForViewModelsOnly() => fx.Ui(() =>
    {
        var locator = new ViewLocator();
        var model = new StatusViewModel("c", "l", () => { });

        Assert.IsType<StatusView>(locator.Build(model));
        Assert.True(locator.Match(model));
        Assert.False(locator.Match("text"));
    });
}
