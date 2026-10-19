using Dictum.App.Shell;

namespace Dictum.App.Tests.Shell;

public sealed class StartGateTests
{
    private const int Races = 20_000;

    [Fact]
    public void ACommandBeforeTheStartWaitsForItAndOneAfterIsCarriedOutAtOnce()
    {
        var gate = new StartGate();

        Assert.False(gate.Request(PendingCommand.ShowSettings));
        Assert.Equal(PendingCommand.ShowSettings, gate.Open());
        Assert.True(gate.Request(PendingCommand.Quit));
    }

    // A --quit that arrives while the shell starts must be carried out by one side: the start or the command.
    [Fact]
    public async Task ACommandRacingTheStartIsNeverLost()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var race = 0; race < Races; race++)
        {
            var gate = new StartGate();
            using var together = new Barrier(2);
            var command = Task.Run(() =>
            {
                together.SignalAndWait(ct);
                return gate.Request(PendingCommand.Quit);
            }, ct);
            together.SignalAndWait(ct);
            var waiting = gate.Open();
            var actedAtOnce = await command;

            Assert.True(actedAtOnce != waiting.HasFlag(PendingCommand.Quit), $"race {race}: carried out at once {actedAtOnce}, waiting {waiting}");
        }
    }
}
