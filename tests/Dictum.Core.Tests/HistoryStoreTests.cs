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

    [Fact]
    public void NewFieldsRoundTripAndOldLinesStillLoad()
    {
        using var dir = new TempDirectory();
        var path = dir.File("history.jsonl");
        File.WriteAllText(path, """{"at":"2026-10-01T10:00:00+03:00","profileId":"p","provider":"stt","rawText":"a","text":"a","language":"uk","audioSeconds":1.5}""" + "\n");
        var store = new HistoryStore(path, maxEntries: 10);

        store.Append(Entry("b") with { Model = "ggml-large-v3-turbo-q8_0.bin", Backend = "Vulkan", TranscribeMs = 6100 });

        var entries = store.ReadLast(10);
        Assert.Null(entries[0].Model);
        Assert.Equal(("ggml-large-v3-turbo-q8_0.bin", "Vulkan", (long?)6100), (entries[1].Model, entries[1].Backend, entries[1].TranscribeMs));
    }

    [Fact]
    public void DeleteRemovesOnlyThatEntry()
    {
        using var dir = new TempDirectory();
        var store = new HistoryStore(dir.File("history.jsonl"), maxEntries: 10);
        var first = Entry("a") with { At = DateTimeOffset.Parse("2026-10-01T10:00:00.1234567+03:00") };
        var second = Entry("b") with { At = first.At.AddSeconds(5) };
        store.Append(first);
        store.Append(second);

        Assert.True(store.Delete(second.At));
        Assert.False(store.Delete(second.At));
        Assert.Equal(["a"], store.ReadLast(10).Select(e => e.Text));
    }

    private static HistoryEntry Entry(string text) =>
        new(DateTimeOffset.Now, "p", "stt", text, text, "uk", 1.5);
}
