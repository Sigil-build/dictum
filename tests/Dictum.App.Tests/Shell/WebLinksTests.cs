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
