using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Plugins;

namespace Dictum.Cli.Tests;

/// <summary>What the hosts that one CLI invocation builds are given.</summary>
public sealed class CliContextTests
{
    // Ledger 92: a host for a config without a plugin provider still runs one made ad hoc, with the invocation's own
    // plugin host, rather than saying that only the daemon and the CLI start plugins.
    [Fact]
    public async Task AnAdHocPluginProviderOnAConfigWithoutOneGetsThePluginHost()
    {
        using var console = new ConsoleCapture();
        await using var context = new CliContext(CliApp.BuildRoot().Parse(["doctor"]));
        await using var host = context.CreateHost(new DictumConfig());

        var stt = host.CreateAdHocStt("p", new PluginSttConfig { Plugin = "test", Model = "m1" });

        Assert.IsType<PluginStt>(stt);
    }

    // The context's own log keeps that warning off the console it writes to, and prints the others there.
    [Fact]
    public async Task APluginsFirstStartWarningNeverReachesTheConsole()
    {
        using var console = new ConsoleCapture();
        await using (var context = new CliContext(CliApp.BuildRoot().Parse(["doctor"])))
        {
            var firstStart = new PluginStartFailedException(new PluginUnavailableException("Plugin p has not been confirmed."));
            context.Log.Warn("Plugin p has not been confirmed.", firstStart);
            context.Log.Warn("Plugin q kept stopping.");
        }

        Assert.DoesNotContain("Plugin p has not been confirmed.", console.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("warning: Plugin q kept stopping.", console.Error.ToString(), StringComparison.Ordinal);
    }

    // In a daemon no command reports it on this console: the failure goes to whoever asked, in another process, or to
    // nobody, as in a warm-up. So its own console shows that warning too.
    [Fact]
    public async Task ADaemonsConsoleShowsAPluginsFirstStartWarning()
    {
        using var console = new ConsoleCapture();
        await using (var context = new CliContext(CliApp.BuildRoot().Parse(["daemon"]), LogLevel.Info))
        {
            var firstStart = new PluginStartFailedException(new PluginUnavailableException("Plugin p has not been confirmed."));
            context.Log.Warn("Plugin p has not been confirmed.", firstStart);
        }

        Assert.Contains("Plugin p has not been confirmed.", console.Error.ToString(), StringComparison.Ordinal);
    }

    // A command reports a first start's failure itself, so only that warning stays off its console. A restart's failure
    // reaches no command, and so does any other warning, whatever its exception.
    [Fact]
    public void OnlyAPluginsFirstStartWarningStaysOffTheConsole()
    {
        var firstStart = new PluginStartFailedException(new PluginUnavailableException("Plugin p has not been confirmed."));

        Assert.False(CliContext.PrintsOnConsole(LogLevel.Warning, "first start", firstStart));
        Assert.True(CliContext.PrintsOnConsole(LogLevel.Warning, "restart", null));
        Assert.True(CliContext.PrintsOnConsole(LogLevel.Warning, "restart", new PluginUnavailableException("kept stopping")));
        Assert.True(CliContext.PrintsOnConsole(LogLevel.Error, "first start", firstStart));
    }
}
