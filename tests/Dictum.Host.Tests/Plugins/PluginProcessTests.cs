using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Dictum.Core.Diagnostics;
using Dictum.Core.Http;
using Dictum.Plugins;
using Dictum.Testing;
using Dictum.TestPlugin;

namespace Dictum.Host.Tests.Plugins;

/// <summary>One plugin process under protocol 1, played by copies of the test plugin.</summary>
public sealed class PluginProcessTests : IDisposable
{
    private const string Hint = "See its output above, and the plugin's own documentation.";

    /// <summary>For waits on the plugin's output, which a busy runner may deliver late.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();
    private readonly HttpClient _http = ProviderHttp.CreateClient();

    public void Dispose()
    {
        _http.Dispose();
        _dir.Dispose();
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task StartsAndAnswersOnALoopbackAddress()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var plugin = await StartAsync(TestPlugins.Launch(TestPlugins.Folder(_dir)), ct);

        Assert.Equal("127.0.0.1", plugin.Endpoint.Url.Host);
        Assert.Equal(1, plugin.Endpoint.Info.Protocol);
        Assert.Equal("Test", plugin.Endpoint.Info.Name);
        Assert.Null(plugin.Endpoint.Info.Model);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task TheKeyComesInTheEnvironmentNotOnTheCommandLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir);
        var model = TestPlugins.Model(_dir, new TestPluginBehaviour());

        var withModel = await InfoAsync(TestPlugins.Launch(folder, model, threads: 3), ct);
        var bare = await InfoAsync(TestPlugins.Launch(folder), ct);

        Assert.Equal(["--port", "0", "--model", model, "--threads", "3"], withModel.Argv);
        Assert.Equal(3, withModel.Threads);
        Assert.Equal(["--port", "0"], bare.Argv);
        Assert.Null(bare.Threads);
        foreach (var (argv, key) in new[] { (withModel.Argv, withModel.Key), (bare.Argv, bare.Key) })
        {
            Assert.Matches("^[0-9a-f]{64}$", key);
            Assert.DoesNotContain(argv, argument => argument.Contains(key, StringComparison.Ordinal));
        }
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task WaitsWhileTheModelLoads()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { LoadMs = 800 });
        var clock = Stopwatch.StartNew();

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct);

        Assert.True(clock.ElapsedMilliseconds >= 800, $"ready after {clock.ElapsedMilliseconds} ms");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AProgramThatExitsWhileStartingReportsItsCodeAndOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ExitAtStart = 3, Stderr = ["no CUDA here"] });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal("Plugin test exited with code 3 while starting.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.Contains("no CUDA here", error.Output);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AProgramThatNeverAnnouncesItselfTimesOutAndIsEnded()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { NoReadyLine = true });
        var timings = PluginTimings.Default with { StartTimeout = TimeSpan.FromSeconds(1) };

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct, timings));

        Assert.Equal("Plugin test did not get ready within 1 s.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPluginRuns(), "the plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnAddressOffThisComputerIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ReadyUrl = "http://192.0.2.1:80" });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal("Plugin test announced http://192.0.2.1:80, which is not on this computer.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPluginRuns(), "the plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnotherProtocolIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { InfoProtocol = 2 });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal("Plugin test speaks protocol 2; this Dictum speaks protocol 1.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPluginRuns(), "the plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task OutputGoesToTheLogUnderThePluginsName()
    {
        var ct = TestContext.Current.CancellationToken;
        var behaviour = new TestPluginBehaviour { Banner = "warming up", AfterReady = "listening", Stderr = ["note"] };
        var folder = TestPlugins.Folder(_dir, behaviour: behaviour);

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct);
        await Eventually.WaitForAsync(() => _log.Has("plugin test: listening"), "the line after the ready line", Patience);

        Assert.Contains((LogLevel.Info, "plugin test: warming up"), _log.Lines);
        Assert.Contains((LogLevel.Info, "plugin test: listening"), _log.Lines);
        Assert.Contains((LogLevel.Info, "plugin test: note"), _log.Lines);
        Assert.Equal(["listening", "note", "warming up"], plugin.RecentOutput.Order(StringComparer.Ordinal));
    }

    /// <summary>The flood ends on both streams only if both pipes are read, since the plugin writes them one after the other.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AFloodOfOutputDoesNotBlockTheStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { FloodLines = 50_000 });

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct);
        await Eventually.WaitForAsync(
            () => _log.Has("plugin test: out 50000") && _log.Has("plugin test: err 50000"), "the end of both floods", Patience);

        Assert.Equal(20, plugin.RecentOutput.Count);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task StopClosesStdinAndAPoliteProgramExitsByItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var plugin = await StartAsync(TestPlugins.Launch(TestPlugins.Folder(_dir)), ct);
        var clock = Stopwatch.StartNew();

        await plugin.StopAsync().WaitAsync(ct);

        Assert.True(clock.Elapsed < PluginTimings.Default.StopGrace, $"stopped after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(0, await plugin.Exited);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AProgramThatIgnoresStdinIsKilledAfterTheGrace()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { IgnoreStdin = true });
        var plugin = await StartAsync(TestPlugins.Launch(folder), ct, PluginTimings.Default with { StopGrace = TimeSpan.FromMilliseconds(500) });

        await plugin.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.True(plugin.Exited.IsCompleted);
        Assert.False(TestPluginRuns(), "the plugin still runs");
    }

    /// <summary>The folder and the model path reach the program whole, since every argument goes through ArgumentList.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AFolderWithSpacesAndCyrillicWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, folderName: "плагін з пробілом");
        var model = TestPlugins.Model(_dir, new TestPluginBehaviour(), "модель з пробілом.json");

        await using var plugin = await StartAsync(TestPlugins.Launch(folder, model), ct);

        Assert.Equal("Test", plugin.Endpoint.Info.Name);
        Assert.Equal("модель з пробілом", plugin.Endpoint.Info.Model?.Id);
    }

    private Task<PluginProcess> StartAsync(PluginLaunch launch, CancellationToken ct, PluginTimings? timings = null) =>
        PluginProcess.StartAsync(launch, _http, _log, timings ?? PluginTimings.Default, ct);

    /// <summary>Starts the plugin, asks <c>/dictum/v1/info</c> directly with the endpoint's key, and stops it.</summary>
    private async Task<(string[] Argv, int? Threads, string Key)> InfoAsync(PluginLaunch launch, CancellationToken ct)
    {
        await using var plugin = await StartAsync(launch, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(plugin.Endpoint.Url, "/dictum/v1/info"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plugin.Endpoint.Key);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var argv = info.RootElement.GetProperty("argv").EnumerateArray().Select(argument => argument.GetString()!).ToArray();
        var threads = info.RootElement.GetProperty("threads") is { ValueKind: JsonValueKind.Number } number ? number.GetInt32() : (int?)null;
        return (argv, threads, plugin.Endpoint.Key);
    }

    /// <summary>Whether a test plugin from this test's folder still runs; every test copies the plugin into its own.</summary>
    private bool TestPluginRuns()
    {
        var mine = Path.DirectorySeparatorChar + Path.GetFileName(_dir.Path) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("dictum-test-plugin"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.MainModule?.FileName.Contains(mine, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // It exited while we looked.
                }
            }
        }

        return false;
    }
}
