using Dictum.Core.Diagnostics;

namespace Dictum.Core.Tests;

public class DictumLogTests
{
    [Fact]
    public void ConsoleGetsOnlyMessagesAtOrAboveItsLevel()
    {
        var console = new StringWriter();
        using var log = new DictumLog(LogLevel.Warning, includeText: false, console, logsDirectory: null);

        log.Info("hidden");
        log.Warn("shown");

        Assert.Equal($"warning: shown{Environment.NewLine}", console.ToString());
    }

    [Fact]
    public void FileGetsEveryLevel()
    {
        using var dir = new TempDirectory();
        using (var log = new DictumLog(LogLevel.Error, includeText: false, console: null, dir.Path))
        {
            log.Debug("first");
            log.Error("second");
        }

        var lines = File.ReadAllLines(dir.File("dictum.log"));
        Assert.Collection(
            lines,
            l => Assert.EndsWith("DEBUG first", l),
            l => Assert.EndsWith("ERROR second", l));
    }

    [Fact]
    public void FileRotatesWhenItGrowsPastTheLimit()
    {
        using var dir = new TempDirectory();
        using (var log = new DictumLog(LogLevel.Error, includeText: false, console: null, dir.Path))
        {
            var chunk = new string('x', 64 * 1024);
            for (var i = 0; i < 20; i++)
            {
                log.Info(chunk);
            }
        }

        Assert.True(File.Exists(dir.File("dictum.log.1")));
        Assert.True(new FileInfo(dir.File("dictum.log")).Length < 1024 * 1024);
    }

    [Theory]
    [InlineData(true, "secret words")]
    [InlineData(false, "<12 chars>")]
    public void RedactHidesTextUnlessVerbose(bool includeText, string expected)
    {
        using var log = new DictumLog(LogLevel.Error, includeText, console: null, logsDirectory: null);

        Assert.Equal(expected, log.Redact("secret words"));
    }

    [Fact]
    public void AnotherFileNameKeepsTwoProcessesApart()
    {
        using var dir = new TempDirectory();
        using (var log = new DictumLog(LogLevel.Error, includeText: false, console: null, dir.Path, "dictum-gui.log"))
        {
            log.Info("from the desktop app");
            Assert.Equal(dir.File("dictum-gui.log"), log.FilePath);
        }

        Assert.False(File.Exists(dir.File("dictum.log")));
        Assert.Contains("from the desktop app", File.ReadAllText(dir.File("dictum-gui.log")));
    }
}
