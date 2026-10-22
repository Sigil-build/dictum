using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.Tests.Updates;
using Dictum.App.Updates;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Setup;

namespace Dictum.App.Tests.Views;

public sealed class AboutViewTests(AvaloniaFixture fx)
{
    [Fact]
    public Task ThePageShowsTheVersionTheSystemAndALinkForEachPlace() => fx.Ui(() =>
    {
        var docs = new List<DocsPage>();
        var urls = new List<string>();
        var page = new AboutPage(new AboutServices("0.4.0", "Windows 11 (10.0.26200), x64", "win-x64", null,
            () => Task.FromResult(new HardwareInfo(null, 0)), new WindowClipboard(() => null), (p, _) => docs.Add(p),
            url => urls.Add(url.AbsoluteUri), NullLog.Instance));

        var window = new Window { Content = new ViewLocator().Build(page), Width = 760, Height = 640 };
        window.Show();

        Assert.IsType<AboutView>(window.Content);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(page.Version, texts);
        Assert.Contains("Windows 11 (10.0.26200), x64", texts);
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        foreach (var label in new[]
        {
            Strings.SettingsAboutDocumentation, Strings.SettingsAboutPrivacy, Strings.SettingsAboutGitHub, Strings.SettingsAboutReleases,
            Strings.SettingsAboutReport, Strings.SettingsAboutLicense, Strings.SettingsAboutNotices,
        })
        {
            buttons.Single(b => Equals(b.Content, label)).Command!.Execute(null);
        }

        Assert.Contains(buttons, b => Equals(b.Content, Strings.SettingsAboutCopy));
        Assert.Equal([DocsPage.Home, DocsPage.Privacy], docs);
        Assert.Equal(5, urls.Count);
        Assert.DoesNotContain(Strings.SettingsAboutUpdates, texts);
        window.Close();
    });

    // Updates: the state, when it was last checked, Check now, and the release page once a newer version is known.
    [Fact]
    public Task TheUpdatesPartShowsTheStateAndChecksNow() => fx.UiAsync(async () =>
    {
        using var dir = new TempDirectory();
        var releases = new FakeReleases { Next = FakeReleases.Found("v0.5.0") };
        using var checker = new UpdateChecker(releases, new UpdateCheckFile(dir.File("update-check.json"), NullLog.Instance), new ManualTimers(),
            TimeProvider.System, "0.4.0", NullLog.Instance);
        var urls = new List<Uri>();
        using var page = new AboutPage(new AboutServices("0.4.0", "Windows 11 (10.0.26200), x64", "win-x64", null,
            () => Task.FromResult(new HardwareInfo(null, 0)), new WindowClipboard(() => null), (_, _) => { }, urls.Add, NullLog.Instance, checker));
        var window = new Window { Content = new ViewLocator().Build(page), Width = 760, Height = 900 };
        window.Show();

        Assert.Contains(Strings.SettingsAboutUpdates, Texts(window));
        Assert.Contains(Strings.SettingsAboutNotChecked, Texts(window));
        Assert.DoesNotContain(Buttons(window), b => Equals(b.Content, Strings.SettingsAboutOpenRelease));

        await page.CheckNowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(page.UpdateState, Texts(window));
        Assert.Contains(page.LastChecked, Texts(window));
        Buttons(window).Single(b => Equals(b.Content, Strings.SettingsAboutOpenRelease)).Command!.Execute(null);
        Assert.Equal([FakeReleases.Page("v0.5.0")], urls);
        Assert.Same(page.CheckNowCommand, Buttons(window).Single(b => Equals(b.Content, Strings.SettingsAboutCheckNow)).Command);
        window.Close();
    });

    private static List<string?> Texts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    private static List<Button> Buttons(Window window) => [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)];
}
