using Dictum.App.Daemon;
using Dictum.App.Tray;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Tray;

public sealed class TrayControllerTests : IAsyncDisposable
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePresenter _presenter = new();
    private readonly FakeCommands _commands = new();

    public ValueTask DisposeAsync() => _presenter.DisposeAsync();

    [Fact]
    public void AttachingShowsWhatHappenedBefore()
    {
        var controller = Controller();
        controller.OnLink(Connected());

        controller.Attach(_presenter);

        Assert.Equal("Dictum · uk · Ctrl+Alt+Space", _presenter.Shown[^1].Tooltip);
    }

    [Fact]
    public void ChoosingAProfileSendsUseProfile()
    {
        var controller = Attached();
        controller.OnLink(Connected());

        _presenter.Click(_presenter.Shown[^1].Menu.First(i => i.Profile == "en"));

        Assert.Equal(["use-profile en"], _commands.Calls);
    }

    [Theory]
    [InlineData(DaemonLinkState.Failed, "restart")]
    [InlineData(DaemonLinkState.Connected, "settings")]
    public void APlainClickRunsTheViewsActivateCommand(DaemonLinkState state, string call)
    {
        var controller = Attached();
        controller.OnLink(state == DaemonLinkState.Connected ? Connected() : new DaemonLink(state));

        _presenter.Activate();

        Assert.Equal([call], _commands.Calls);
    }

    [Fact]
    public void MenuCommandsReachTheShell()
    {
        var controller = Attached();
        controller.OnLink(Connected());
        var menu = _presenter.Shown[^1].Menu;

        foreach (var command in new[] { TrayCommand.PauseHotkeys, TrayCommand.History, TrayCommand.Settings, TrayCommand.Quit })
        {
            _presenter.Click(menu.First(i => i.Command == command));
        }

        Assert.Equal(["pause", "history", "settings", "quit"], _commands.Calls);
    }

    [Fact]
    public void DocumentationOpensTheDocsOnce()
    {
        var controller = Attached();
        controller.OnLink(Connected());

        _presenter.Click(_presenter.Shown[^1].Menu.Single(i => i.Command == TrayCommand.Documentation));

        Assert.Equal(["documentation"], _commands.Calls);
    }

    [Fact]
    public void FramesDoNotRedraw()
    {
        var controller = Attached();
        var before = _presenter.Shown.Count;

        controller.OnEvent(new IpcEvent(IpcEventKind.Frame, Rms: 0.1f));

        Assert.Equal(before, _presenter.Shown.Count);
    }

    [Fact]
    public void ATickRedrawsOnceAHeldErrorExpires()
    {
        var controller = Attached();
        controller.OnLink(Connected());
        controller.OnEvent(IpcEvent.Finished(DictationOutcome.Error, "boom", null, 0, null));
        var shown = _presenter.Shown.Count;

        controller.Tick();
        _clock.Advance(TrayState.ErrorHold);
        controller.Tick();
        controller.Tick();

        Assert.Equal(shown + 1, _presenter.Shown.Count);
        Assert.Equal(TrayGlyph.Idle, _presenter.Shown[^1].Glyph);
    }

    private TrayController Controller() => new(new TrayState(_clock), _commands, _clock);

    private TrayController Attached()
    {
        var controller = Controller();
        controller.Attach(_presenter);
        return controller;
    }

    private static DaemonLink Connected() => new(DaemonLinkState.Connected, new DaemonStatus(
        nameof(PipelineState.Idle), null, [new ProfileStatus("uk", "Ctrl+Alt+Space", "PushToTalk", true), new ProfileStatus("en", null, "Toggle", true)], "h", "p", 7));

    internal sealed class FakePresenter : ITrayPresenter
    {
        public List<TrayView> Shown { get; } = [];

        public event Action<TrayMenuItem>? ItemClicked;

        public event Action? Activated;

        public event Action? AvailabilityChanged;

        public bool IsAvailable { get; set; } = true;

        public void Show(TrayView view) => Shown.Add(view);

        public void Refresh()
        {
        }

        public void Click(TrayMenuItem item) => ItemClicked?.Invoke(item);

        public void Activate() => Activated?.Invoke();

        public void Lose()
        {
            IsAvailable = false;
            AvailabilityChanged?.Invoke();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCommands : ITrayCommands
    {
        public List<string> Calls { get; } = [];

        public void UseProfile(string profile) => Calls.Add($"use-profile {profile}");

        public void PauseHotkeys() => Calls.Add("pause");

        public void ResumeHotkeys() => Calls.Add("resume");

        public void ShowSettings() => Calls.Add("settings");

        public void ShowHistory() => Calls.Add("history");

        public void ShowDocumentation() => Calls.Add("documentation");

        public void RestartDaemon() => Calls.Add("restart");

        public void Quit() => Calls.Add("quit");
    }
}
