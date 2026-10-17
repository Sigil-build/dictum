using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;

namespace Dictum.App.Tests.Settings;

public sealed class HistoryPageTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeClipboard _clipboard = new();

    public HistoryPageTests() => Directory.CreateDirectory(_dir);

    private string File => Path.Combine(_dir, "history.jsonl");

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

    private void Dictated(int minute, string raw, string text, string profile = "uk", string? language = "uk", string? error = null) =>
        new HistoryStore(File, 200).Append(new HistoryEntry(Start.AddMinutes(minute), profile, "local", raw, text, language, 2.46, error));

    private HistoryPage Open(int historySize = 200)
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
        store.Save(new DictumConfig { General = new GeneralSettings { HistorySize = historySize } });
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var page = new HistoryPage(session, File, _clipboard, NullLog.Instance);
        page.OnShown();
        return page;
    }

    private void ThreeDictations()
    {
        Dictated(1, "привіт світ", "Привіт, світ.");
        Dictated(2, "hello there", "Hello there.", profile: "en", language: "en");
        Dictated(3, "третій запис\nдругий рядок", "Третій запис.\nДругий рядок.");
    }

    [Fact]
    public void TheNewestComesFirstAndIsSelected()
    {
        ThreeDictations();

        var page = Open();

        Assert.Equal(["Третій запис.", "Hello there.", "Привіт, світ."], page.Items.Select(i => i.FirstLine));
        Assert.Equal(page.Items[0], page.Selected);
        Assert.Equal(("uk", "UK", "2.5 s"), (page.Items[0].Profile, page.Items[0].Language, page.Items[0].Duration));
        Assert.Null(page.Empty);
    }

    [Fact]
    public void TheHistorySizeIsHonoured()
    {
        ThreeDictations();

        Assert.Equal(2, Open(historySize: 2).Items.Count);
    }

    [Fact]
    public void HistoryOffShowsNothingAndSaysSo()
    {
        ThreeDictations();

        var page = Open(historySize: 0);

        Assert.Empty(page.Items);
        Assert.Equal(Strings.SettingsHistoryOff, page.Empty);
    }

    [Fact]
    public void WithoutAFileThereIsNothingYet()
    {
        var page = Open();

        Assert.Empty(page.Items);
        Assert.Equal(Strings.SettingsHistoryNone, page.Empty);
        Assert.Null(page.Error);
    }

    [Fact]
    public void SearchIgnoresCaseAndLooksAtBothTextsAndTheProfile()
    {
        ThreeDictations();
        var page = Open();

        page.Search = "ПРИВІТ";
        Assert.Equal(["Привіт, світ."], page.Items.Select(i => i.FirstLine));

        page.Search = "hello there";
        Assert.Single(page.Items);

        page.Search = "en";
        Assert.Equal(["Hello there."], page.Items.Select(i => i.FirstLine));
    }

    [Fact]
    public void ASearchWithoutMatchesSaysSo()
    {
        ThreeDictations();
        var page = Open();

        page.Search = "zebra";

        Assert.Empty(page.Items);
        Assert.Null(page.Selected);
        Assert.Equal("Nothing matches “zebra”.", page.Empty);
    }

    [Fact]
    public async Task CopyPutsTheFinalTextOnTheClipboard()
    {
        ThreeDictations();
        var page = Open();

        await page.CopyCommand.ExecuteAsync(page.Items[1]);

        Assert.Equal(["Hello there."], _clipboard.Texts);
    }

    // Final review I3: a busy clipboard throws; the page says so instead of crashing dictum-gui.
    [Fact]
    public async Task ACopyThatFailsIsShownNotThrown()
    {
        ThreeDictations();
        var page = Open();
        _clipboard.Fails = new InvalidOperationException("The clipboard is in use by another application.");

        await page.CopyCommand.ExecuteAsync(page.Items[0]);

        Assert.Equal("The text could not be copied: The clipboard is in use by another application.", page.Error);
    }

    [Fact]
    public void DeleteRemovesTheEntryFromTheFile()
    {
        ThreeDictations();
        var page = Open();

        page.DeleteCommand.Execute(page.Items[1]);

        Assert.Equal(["Третій запис.", "Привіт, світ."], page.Items.Select(i => i.FirstLine));
        Assert.Equal(2, new HistoryStore(File, 200).ReadLast(10).Count);
        Assert.Equal(page.Items[0], page.Selected);
    }

    [Fact]
    public void AFailedDictationShowsWhy()
    {
        Dictated(1, "raw", "raw", error: "cleanup: timed out");

        var item = Open().Items[0];

        Assert.Equal("Failed: cleanup: timed out", item.Problem);
    }

    [Fact]
    public void ShowingThePageAgainReadsNewDictations()
    {
        Dictated(1, "один", "Один.");
        var page = Open();
        Dictated(2, "два", "Два.");

        page.OnShown();

        Assert.Equal(2, page.Items.Count);
    }

    private sealed class FakeClipboard : ITextClipboard
    {
        public List<string> Texts { get; } = [];

        public Exception? Fails { get; set; }

        public Task SetTextAsync(string text)
        {
            if (Fails is not null)
            {
                return Task.FromException(Fails);
            }

            Texts.Add(text);
            return Task.CompletedTask;
        }
    }
}
