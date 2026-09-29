using Dictum.Core.History;

namespace Dictum.Core.Tests;

public class HistoryStoreTests
{
    [Fact]
    public void KeepsOnlyTheLastEntries()
    {
        using var dir = new TempDirectory();
        var store = new HistoryStore(dir.File("history.jsonl"), maxEntries: 3);

        for (var i = 0; i < 5; i++)
        {
            store.Append(Entry($"text {i}"));
        }

        Assert.Equal(["text 2", "text 3", "text 4"], store.ReadLast(10).Select(e => e.Text));
        Assert.Equal(["text 4"], store.ReadLast(1).Select(e => e.Text));
    }

    [Fact]
    public void ZeroSizeDisablesHistory()
    {
        using var dir = new TempDirectory();
        var store = new HistoryStore(dir.File("history.jsonl"), maxEntries: 0);

        store.Append(Entry("x"));

        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void CorruptLinesAreSkipped()
    {
        using var dir = new TempDirectory();
        var store = new HistoryStore(dir.File("history.jsonl"), maxEntries: 10);
        store.Append(Entry("good"));
        File.AppendAllText(store.Path, "{not json\n");

        Assert.Equal("good", Assert.Single(store.ReadLast(10)).Text);
    }

    private static HistoryEntry Entry(string text) =>
        new(DateTimeOffset.Now, "p", "stt", text, text, "uk", 1.5);
}
