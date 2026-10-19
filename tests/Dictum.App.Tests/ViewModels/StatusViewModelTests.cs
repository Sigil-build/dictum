using Dictum.App.Daemon;
using Dictum.App.ViewModels;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.ViewModels;

public class StatusViewModelTests
{
    [Fact]
    public void ConnectedShowsThePidAndTheProfiles()
    {
        var model = Model();

        model.OnLink(new DaemonLink(DaemonLinkState.Connected, new DaemonStatus(
            nameof(PipelineState.Idle), null, [new ProfileStatus("uk", "Ctrl+Alt+Space", "PushToTalk", true), new ProfileStatus("en", null, "Toggle", true)], "h", "p", 4242)));

        Assert.Equal("Connected to the daemon (pid 4242)", model.Headline);
        Assert.Equal(["uk · Ctrl+Alt+Space", "en"], model.Profiles);
        Assert.False(model.CanRestart);
        Assert.Null(model.Problem);
    }

    [Theory]
    [InlineData(DaemonLinkState.Failed, true)]
    [InlineData(DaemonLinkState.VersionMismatch, true)]
    [InlineData(DaemonLinkState.Restarting, false)]
    public void ProblemsAreShownAndSomeOfferARestart(DaemonLinkState state, bool canRestart)
    {
        var model = Model();

        model.OnLink(new DaemonLink(state, Problem: "details are in dictum-gui.log"));

        Assert.Equal((canRestart, "details are in dictum-gui.log"), (model.CanRestart, model.Problem));
        Assert.Empty(model.Profiles);
    }

    [Fact]
    public void TheRestartButtonRestarts()
    {
        var restarts = 0;
        var model = new StatusViewModel("/c/config.json", "/d/dictum-gui.log", () => restarts++);

        model.RestartCommand.Execute(null);

        Assert.Equal(1, restarts);
        Assert.Equal(("Config: /c/config.json", "Log: /d/dictum-gui.log"), (model.ConfigLine, model.LogLine));
    }

    private static StatusViewModel Model() => new("config.json", "dictum-gui.log", () => { });
}
