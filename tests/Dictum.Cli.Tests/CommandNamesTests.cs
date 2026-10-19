using System.CommandLine;

namespace Dictum.Cli.Tests;

/// <summary>
/// The names scripts and compositor bindings call, spelled out. The control verbs are the daemon's IPC command
/// constants themselves, so only literal names catch a changed value.
/// </summary>
public sealed class CommandNamesTests
{
    [Fact]
    public void TheRootCommandKeepsItsVerbs() =>
        Assert.Equal(
            [
                "cancel", "config", "daemon", "devices", "doctor", "history", "init", "models", "process", "profiles", "record",
                "reload", "secret", "shutdown", "start", "status", "stop", "stt", "toggle", "transcribe", "vocab",
            ],
            CliApp.BuildRoot().Subcommands.Select(c => c.Name).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("toggle")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("cancel")]
    [InlineData("reload")]
    [InlineData("shutdown")]
    public void EachControlVerbTakesAProfile(string verb)
    {
        var profile = Assert.Single(Verb(verb).Options);

        Assert.Equal("--profile", profile.Name);
        Assert.Equal(["-p"], profile.Aliases);
    }

    [Fact]
    public void StatusPrintsJsonOrWatches() =>
        Assert.Equal(["--json", "--watch", "--frames"], Verb("status").Options.Select(o => o.Name));

    private static Command Verb(string name) => CliApp.BuildRoot().Subcommands.Single(c => c.Name == name);
}
