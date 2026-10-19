using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public sealed class PastingViewTests(AvaloniaFixture fx) : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public Task TypingACommaAddsTheAppAndClearsTheBox() => fx.Ui(() =>
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var window = new Window { Content = new ViewLocator().Build(new PastingPage(session, "General", linux: true)), Width = 900, Height = 900 };
        window.Show();
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "AppBox");

        box.Focus();
        window.KeyTextInput("my-term,");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("my-term", session.Config.General.TerminalApps);
        Assert.Equal("", box.Text);
        window.Close();
    });
}
