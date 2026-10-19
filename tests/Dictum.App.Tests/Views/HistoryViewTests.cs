using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;

namespace Dictum.App.Tests.Views;

public sealed class HistoryViewTests(AvaloniaFixture fx) : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public Task TheListAndBothTextsShow() => fx.Ui(() =>
    {
        var file = _dir.File("history.jsonl");
        new HistoryStore(file, 200).Append(new HistoryEntry(DateTimeOffset.Now, "uk", "local", "привіт світ", "Привіт, світ.", "uk", 1.2));
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var window = new Window { Width = 900, Height = 600 };
        var page = new HistoryPage(session, file, new WindowClipboard(() => window), new FakeRunner(), NullLog.Instance);
        page.OnShown();
        window.Content = new ViewLocator().Build(page);

        window.Show();

        Assert.IsType<HistoryView>(window.Content);
        Assert.Equal(1, window.GetVisualDescendants().OfType<ListBox>().Single().ItemCount);
        var texts = window.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("привіт світ", texts);
        Assert.Contains("Привіт, світ.", texts);
        window.Close();
    });

    [Fact]
    public Task TheWindowClipboardTakesTheText() => fx.UiAsync(async () =>
    {
        var window = new Window();
        window.Show();

        await new WindowClipboard(() => window).SetTextAsync("Привіт, світ.");

        Assert.Equal("Привіт, світ.", await window.Clipboard!.TryGetTextAsync());
        window.Close();
    });
}
