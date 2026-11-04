using Dictum.Core.Configuration;
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
}
