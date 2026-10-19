using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public sealed class SpeechProvidersViewTests(AvaloniaFixture fx) : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public Task CardsShowTheirFieldsAndLeavingTheKeyFieldStoresIt() => fx.Ui(() =>
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(ProfilesPageTests.Sample());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var secrets = new FakeSecrets();
        var page = new SpeechProvidersPage(session, new SpeechServices(new FakeChannel(), new FrameHub(TimeProvider.System), new ManualTimers(),
            secrets, _ => [new("ggml-large-v3-turbo-q8_0.bin", "Turbo")], _ => true, _ => Task.CompletedTask, TimeProvider.System, NullLog.Instance));
        var other = new Button();
        var window = new Window { Content = new DockPanel { Children = { other, new ViewLocator().Build(page)! } }, Width = 960, Height = 1400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(window.GetVisualDescendants().OfType<SpeechProvidersView>());
        var key = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "KeyBox" && t.IsEffectivelyVisible);
        key.Focus();
        key.Text = "sk-123";
        other.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sk-123", secrets.Values["dictum-stt-remote"]);
        Assert.Equal("", key.Text);
        window.Close();
    });
}
