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

    // Final review C1: the real app box clears once the app is added.
    [Fact]
    public Task TypingACommaAddsTheAppAndClearsTheBox() => fx.Ui(() =>
    {
        Directory.CreateDirectory(_dir);
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
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
