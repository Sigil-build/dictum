using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dictum.App.Controls;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views;
using Dictum.App.Views.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.App.Tests.Views;

public class SystemViewTests(AvaloniaFixture fx)
{
    [Fact]
    public Task TheRowsTheCardAndTheBannerShow() => fx.Ui(() =>
    {
        var channel = new FakeChannel
        {
            Answer = _ => new IpcResponse(true, Checks: [new PlatformCheck("paste", new HealthStatus(HealthLevel.Error, "SendInput failed"))]),
        };
        var page = new SystemPage(new SystemServices(channel, new StatusViewModel("c", "l", () => { }),
            () => new SystemRow("Listening overlay", HealthLevel.Ok, "Shows while you dictate"),
            () => new SystemRow("Tray icon", HealthLevel.Warning, "Hidden", "Pin it.", "Open tray settings", null),
            new ManualTimers(), TimeProvider.System, "Windows 11", (_, _) => { }));
        page.OnShown();

        var window = new Window { Content = new ViewLocator().Build(page), Width = 760, Height = 900 };
        window.Show();

        Assert.IsType<SystemView>(window.Content);
        Assert.Single(window.GetVisualDescendants().OfType<StatusView>());
        Assert.Equal(3, window.GetVisualDescendants().OfType<StatusBadge>().Count());
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Problems: 1. Warnings: 1.", texts);
        Assert.Contains("SendInput failed", texts);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Open tray settings") && b.IsEffectivelyVisible);
        page.OnHidden();
        window.Close();
    });

    // "How to fix" shows under the status of a doctor row with a problem, and nowhere else.
    [Fact]
    public Task HowToFixShowsUnderAProblemRowsStatus() => fx.Ui(() =>
    {
        var opened = new List<(DocsPage Page, string? Anchor)>();
        var channel = new FakeChannel
        {
            Answer = _ => new IpcResponse(true, Checks:
            [
                new PlatformCheck("config", new HealthStatus(HealthLevel.Ok, "config.json")),
                new PlatformCheck("paste", new HealthStatus(HealthLevel.Error, "SendInput failed")),
            ]),
        };
        var page = new SystemPage(new SystemServices(channel, new StatusViewModel("c", "l", () => { }),
            () => new SystemRow("Listening overlay", HealthLevel.Ok, "Shows while you dictate"),
            () => new SystemRow("Tray icon", HealthLevel.Warning, "Hidden"),
            new ManualTimers(), TimeProvider.System, "Windows 11", (docs, anchor) => opened.Add((docs, anchor))));
        page.OnShown();

        var window = new Window { Content = new ViewLocator().Build(page), Width = 760, Height = 900 };
        window.Show();

        var links = window.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, Strings.SettingsSystemHelp) && b.IsEffectivelyVisible).ToList();
        var link = Assert.Single(links);
        var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "SendInput failed");
        Visual[] reading = [status, link];
        Assert.Equal(reading, window.GetVisualDescendants().Where(reading.Contains));
        link.Command!.Execute(null);

        Assert.Equal([(DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Pasting)], opened);
        page.OnHidden();
        window.Close();
    });
}
