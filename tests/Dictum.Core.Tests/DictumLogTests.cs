using Dictum.Core.Diagnostics;
using Dictum.Testing;

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

    /// <summary>A message the console is told to skip, because the command reports it itself, is still in the file.</summary>
    [Fact]
    public void AMessageTheConsoleSkipsIsStillInTheFile()
    {
        using var dir = new TempDirectory();
        var console = new StringWriter();
        using (var log = new DictumLog(LogLevel.Warning, includeText: false, console, dir.Path)
        {
            ConsoleFilter = (_, _, exception) => exception is null,
        })
        {
            log.Warn("skipped", new InvalidOperationException());
            log.Warn("shown");
        }

        Assert.Equal($"warning: shown{Environment.NewLine}", console.ToString());
        Assert.Collection(
            File.ReadAllLines(dir.File("dictum.log")).Where(l => !l.StartsWith("System.", StringComparison.Ordinal) && !l.StartsWith("   at", StringComparison.Ordinal)),
            l => Assert.EndsWith("WARNING skipped", l),
            l => Assert.EndsWith("WARNING shown", l));
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
    public void HiddenCharactersInAMessageOrAnExceptionReachTheConsoleSpeltOutAndTheFileRaw()
    {
        using var dir = new TempDirectory();
        var console = new StringWriter();
        using (var log = new DictumLog(LogLevel.Debug, includeText: false, console, dir.Path))
        {
            log.Warn("from \u001b[2Jserver", new InvalidOperationException("bad \u001b]0;PWNED\u0007 header"));
        }

        var shown = console.ToString();
        Assert.DoesNotContain('\u001b', shown);
        Assert.DoesNotContain('\u0007', shown);
        Assert.Contains("from \\u001b[2Jserver", shown, StringComparison.Ordinal);
        Assert.Contains("bad \\u001b]0;PWNED\\u0007 header", shown, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, shown, StringComparison.Ordinal);
        var file = File.ReadAllText(dir.File("dictum.log"));
        Assert.Contains("from \u001b[2Jserver", file, StringComparison.Ordinal);
        Assert.Contains("bad \u001b]0;PWNED\u0007 header", file, StringComparison.Ordinal);
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
