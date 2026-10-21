using System.ComponentModel;
using System.Diagnostics;
using Dictum.App.Shell;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Shell;

public sealed class WebLinksTests
{
    private static readonly Uri Url = new("https://docs.sigil.build/dictum/troubleshooting/#hotkeys");

    [Fact]
    public void TheShellOpensTheUrl()
    {
        ProcessStartInfo? started = null;

        var opened = WebLinks.Open(Url, NullLog.Instance, info =>
        {
            started = info;
            return null;
        });

        Assert.True(opened);
        Assert.Equal((Url.AbsoluteUri, true), (started?.FileName, started?.UseShellExecute));
    }

    // A handler that starts and then fails (xdg-open with no https handler) is invisible to Dictum, so the log names
    // the URL before the browser starts, and the user can always find it there.
    [Fact]
    public void TheLogNamesTheUrlBeforeTheBrowserStarts()
    {
        var log = new ListLog();
        var loggedFirst = false;

        var opened = WebLinks.Open(Url, log, _ =>
        {
            loggedFirst = log.Lines.Any(l => l.Level == LogLevel.Info && l.Message.Contains(Url.AbsoluteUri, StringComparison.Ordinal));
            return null;
        });

        Assert.True(opened);
        Assert.True(loggedFirst, string.Join(Environment.NewLine, log.Lines.Select(l => $"{l.Level}: {l.Message}")));
    }

    // No browser opens: xdg-open is missing, or the AppImage's environment breaks the handler. The click does nothing
    // more, and the log names the URL, so the user can find it there.
    [Fact]
    public void ABrowserThatCannotStartIsAWarningNamingTheUrl()
    {
        var log = new ListLog();

        var opened = WebLinks.Open(Url, log, _ => throw new Win32Exception(2, "No such file or directory"));

        Assert.False(opened);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains(Url.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public void AShellThatRefusesIsAWarningToo()
    {
        var log = new ListLog();

        var opened = WebLinks.Open(Url, log, _ => throw new InvalidOperationException("No file name was specified."));

        Assert.False(opened);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains(Url.AbsoluteUri, StringComparison.Ordinal));
    }
}
