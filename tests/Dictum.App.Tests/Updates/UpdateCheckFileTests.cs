using Dictum.App.Updates;

namespace Dictum.App.Tests.Updates;

public sealed class UpdateCheckFileTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();

    public void Dispose() => _dir.Dispose();

    private string Path => _dir.File("update-check.json");

    [Fact]
    public void WhatIsSavedIsReadBack()
    {
        var state = new UpdateCheckState
        {
            LastChecked = new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.FromHours(3)),
            Latest = "0.5.0",
            Page = "https://github.com/Sigil-build/dictum/releases/tag/v0.5.0",
            Notified = "0.5.0",
        };

        new UpdateCheckFile(Path, _log).Save(state);

        Assert.Equal(state, new UpdateCheckFile(Path, _log).Load());
        Assert.Contains("\"lastChecked\": \"2026-10-22T09:30:00+03:00\"", File.ReadAllText(Path));
        Assert.Contains("\"notified\": \"0.5.0\"", File.ReadAllText(Path));
    }

    [Fact]
    public void NoFileIsNoCheckYet() => Assert.Equal(new UpdateCheckState(), new UpdateCheckFile(Path, _log).Load());

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "lastChecked": "yesterday" }""")]
    public void ADamagedFileIsNoCheckYet(string text)
    {
        File.WriteAllText(Path, text);

        Assert.Equal(new UpdateCheckState(), new UpdateCheckFile(Path, _log).Load());
        Assert.True(_log.Has("update-check.json"));
    }

    [Fact]
    public void AFileThatCannotBeWrittenIsLoggedNotThrown()
    {
        Directory.CreateDirectory(Path);

        new UpdateCheckFile(Path, _log).Save(new UpdateCheckState { Latest = "0.5.0" });

        Assert.True(_log.Has("update-check.json"));
    }

    [Fact]
    public void TheFolderIsMadeWhenItIsMissing()
    {
        var path = _dir.File(System.IO.Path.Combine("data", "update-check.json"));

        new UpdateCheckFile(path, _log).Save(new UpdateCheckState { Latest = "0.5.0" });

        Assert.Equal("0.5.0", new UpdateCheckFile(path, _log).Load().Latest);
    }
}
