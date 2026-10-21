using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.App.Tests.Settings;

public sealed class SystemPageTests
{
    private readonly FakeChannel _channel = new();
    private readonly ManualTimers _timers = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly StatusViewModel _service = new("config.json", "dictum-gui.log", () => { });
    private readonly SystemRow _overlay = new("Listening overlay", HealthLevel.Ok, "Shows while you dictate");
    private SystemRow _tray = new("Tray icon", HealthLevel.Ok, "Always visible");

    /// <summary>What the rows' "How to fix" opened, in order.</summary>
    private readonly List<(DocsPage Page, string? Anchor)> _opened = [];

    private SystemPage Page() => new(new SystemServices(_channel, _service, () => _overlay, () => _tray, _timers, _clock, "Windows 11",
        (page, anchor) => _opened.Add((page, anchor))));

    /// <summary>Clicks the row's "How to fix" and returns what it opened.</summary>
    private (DocsPage Page, string? Anchor) Help(SystemRow row)
    {
        Assert.NotNull(row.Help);
        row.Help.Execute(null);
        return _opened[^1];
    }

    private void Doctor(params PlatformCheck[] checks) =>
        _channel.Answer = request => request.Command == IpcCommand.Doctor ? new IpcResponse(true, Checks: checks) : new IpcResponse(true);

    [Fact]
    public void TheChecksBecomeRowsFollowedByTheAppsOwn()
    {
        Doctor(
            new PlatformCheck("config", new HealthStatus(HealthLevel.Ok, "config.json (1 STT, 0 processors, 1 profiles)")),
            new PlatformCheck("paste", new HealthStatus(HealthLevel.Error, "SendInput failed", "Run Dictum without administrator rights.")),
            new PlatformCheck("stt:local", new HealthStatus(HealthLevel.Warning, "No answer within 15 s")));
        var page = Page();

        page.OnShown();

        Assert.Equal(["Config file", "Paste", "Speech provider local", "Listening overlay", "Tray icon"], page.Rows.Select(r => r.Name));
        Assert.Equal(new SystemRow("Paste", HealthLevel.Error, "SendInput failed", "Run Dictum without administrator rights."), page.Rows[1] with { Help = null });
        Assert.Equal("Problems: 1. Warnings: 1.", page.Summary);
        Assert.True(page.HasProblems);
        Assert.Equal(SystemPage.DoctorTimeout, _channel.LastTimeout);
    }

    [Fact]
    public void WithoutTheDaemonTheChecksSaySo()
    {
        _channel.Link = new DaemonLink(DaemonLinkState.Restarting);
        var page = Page();

        page.OnShown();

        Assert.Equal(new SystemRow(Strings.SettingsCheckDaemon, HealthLevel.Error, Strings.SettingsSystemNoDaemon, Strings.SettingsSystemNoDaemonDetail), page.Rows[0] with { Help = null });
        Assert.Equal(["Daemon", "Listening overlay", "Tray icon"], page.Rows.Select(r => r.Name));
        Assert.Equal("Problems: 1. Warnings: 0.", page.Summary);
    }

    [Fact]
    public void TheDaemonComingBackRunsTheChecks()
    {
        _channel.Link = new DaemonLink(DaemonLinkState.Restarting);
        Doctor(new PlatformCheck("daemon", new HealthStatus(HealthLevel.Ok, "running (pid 4242), answering this check")));
        var page = Page();
        page.OnShown();

        _channel.Link = FakeChannel.Connected();
        page.OnLink(_channel.Link);

        Assert.Equal(HealthLevel.Ok, page.Rows[0].Level);
        Assert.Equal(Strings.SettingsSystemAllGood, page.Summary);
        Assert.False(page.HasProblems);
    }

    [Fact]
    public void ARefusedCheckShowsTheDaemonsReason()
    {
        _channel.Answer = _ => IpcResponse.Failure("This daemon cannot run the checks.");
        var page = Page();

        page.OnShown();

        Assert.Equal("This daemon cannot run the checks.", page.Rows[0].Status);
        Assert.Equal((DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Daemon), Help(page.Rows[0]));
    }

    [Fact]
    public void AWarningLinksToItsTroubleshootingSection()
    {
        Doctor(new PlatformCheck("hotkeys", new HealthStatus(HealthLevel.Warning, "No shortcut portal", "Log in to a Plasma or GNOME session.")));
        var page = Page();

        page.OnShown();

        Assert.Equal((DocsPage.Troubleshooting, "hotkeys"), Help(page.Rows[0]));
        Assert.Equal([(DocsPage.Troubleshooting, "hotkeys")], _opened);
    }

    // Each check opens the section that dictum doctor's see: line names; a check with no section of its own opens #doctor.
    [Theory]
    [InlineData("paste (ydotool)", HealthLevel.Error, "pasting")]
    [InlineData("stt:local", HealthLevel.Warning, "speech-recognition")]
    [InlineData("processor:cleanup", HealthLevel.Error, "llm-cleanup")]
    [InlineData("config warnings", HealthLevel.Warning, "config-warnings")]
    [InlineData("something new", HealthLevel.Warning, "doctor")]
    public void EachCheckLinksWhereDoctorDoes(string name, HealthLevel level, string anchor)
    {
        Doctor(new PlatformCheck(name, new HealthStatus(level, "not right")));
        var page = Page();

        page.OnShown();

        Assert.Equal((DocsPage.Troubleshooting, anchor), Help(page.Rows[0]));
    }

    [Fact]
    public void AnOkCheckHasNoHowToFix()
    {
        Doctor(new PlatformCheck("hotkeys", new HealthStatus(HealthLevel.Ok, "low-level keyboard hook (SharpHook)")));
        var page = Page();

        page.OnShown();

        Assert.Null(page.Rows[0].Help);
    }

    [Fact]
    public void ADaemonThatCannotBeReachedLinksToTheDaemonSection()
    {
        _channel.Link = new DaemonLink(DaemonLinkState.Restarting);
        var page = Page();

        page.OnShown();

        Assert.Equal((DocsPage.Troubleshooting, DocsAnchors.Troubleshooting.Daemon), Help(page.Rows[0]));
    }

    // The overlay and tray rows are the app's own, not doctor's: the page links neither, even with a problem.
    [Fact]
    public void TheOverlayAndTrayRowsGetNoHowToFix()
    {
        Doctor();
        _tray = new SystemRow("Tray icon", HealthLevel.Warning, "Hidden in the overflow area");
        var page = Page();

        page.OnShown();

        Assert.Equal(["Listening overlay", "Tray icon"], page.Rows.Select(r => r.Name));
        Assert.All(page.Rows, row => Assert.Null(row.Help));
    }

    [Fact]
    public void TheHeaderCountsTheSecondsSinceTheCheck()
    {
        Doctor();
        var page = Page();
        page.OnShown();
        Assert.Equal("Windows 11 · checked 0 s ago", page.Header);

        _clock.Advance(TimeSpan.FromSeconds(5));
        _timers.Fire();

        Assert.Equal("Windows 11 · checked 5 s ago", page.Header);
    }

    [Fact]
    public async Task CheckAgainRunsTheChecksAgainWithFreshAppRows()
    {
        Doctor();
        var page = Page();
        page.OnShown();
        _tray = new SystemRow("Tray icon", HealthLevel.Warning, "Hidden in the overflow area");

        await page.CheckAgainCommand.ExecuteAsync(null);

        Assert.Equal(2, _channel.Commands.Count(c => c == IpcCommand.Doctor));
        Assert.Equal(HealthLevel.Warning, page.Rows[^1].Level);
    }

    [Fact]
    public void HidingThePageStopsTheClock()
    {
        Doctor();
        var page = Page();
        page.OnShown();

        page.OnHidden();

        Assert.Equal(0, _timers.Pending);
    }

    [Theory]
    [InlineData("hotkeys", "Global hotkey")]
    [InlineData("paste (ydotool)", "Paste (ydotool)")]
    [InlineData("processor:cleanup", "Processor cleanup")]
    [InlineData("something new", "something new")]
    public void CheckNamesAreLocalisedAndUnknownOnesKept(string name, string shown) => Assert.Equal(shown, SystemPage.CheckName(name));

    [Fact]
    public void TheDaemonCardIsTheStatusModel() => Assert.Same(_service, Page().Service);


    // An answer that cannot be read is a problem row, not a crash or an endless "checking".
    [Fact]
    public async Task AnAnswerThatCannotBeReadIsAProblemNotACrash()
    {
        _channel.Throws = new System.Text.Json.JsonException("'<' is an invalid start of a value.");
        var page = Page();

        await page.CheckAgainCommand.ExecuteAsync(null);

        Assert.Equal((HealthLevel.Error, "'<' is an invalid start of a value."), (page.Rows[0].Level, page.Rows[0].Status));
        Assert.False(page.Checking);
    }
}
