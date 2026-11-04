using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dictum.Core;
using Dictum.Core.Diagnostics;
using Dictum.Plugins;
using Dictum.Testing;
using Dictum.TestPlugin;

namespace Dictum.Host.Tests.Plugins;

/// <summary>One plugin process under protocol 1, played by copies of the test plugin.</summary>
public sealed class PluginProcessTests : IDisposable
{
    private const string Hint = "See its output above, and the plugin's own documentation.";

    /// <summary>The file a batch launcher writes when it runs.</summary>
    private const string StartedMark = "started.txt";

    /// <summary>How long a crashing plugin lives after its info answer, so that the crash never lands inside the start.</summary>
    private const int CrashAfterMs = 500;

    /// <summary>For waits on the plugin's output, which a busy runner may deliver late.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>A stop that kills: the grace, up to 5 s for the killed plugin to be gone, and room for a busy runner.</summary>
    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A start that is meant to time out after the ready line: long enough for the plugin to print it on a busy runner,
    /// and for Windows, which tries a refused connection on this computer again for about 2 s, to give up on one.
    /// </summary>
    private static readonly PluginTimings HealthTimeout = PluginTimings.Default with { StartTimeout = TimeSpan.FromSeconds(5) };

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();
    private readonly HttpClient _http = PluginHttp.CreateClient();

    public void Dispose()
    {
        _http.Dispose();
        TestPlugins.EndAllIn(_dir);
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
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnAddressOffThisComputerIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ReadyUrl = "http://192.0.2.1:80" });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal("Plugin test announced http://192.0.2.1:80, which is not on this computer.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnotherProtocolIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { InfoProtocol = 2 });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal("Plugin test speaks protocol 2; this Dictum speaks protocol 1.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task OutputGoesToTheLogUnderThePluginsName()
    {
        var ct = TestContext.Current.CancellationToken;
        var behaviour = new TestPluginBehaviour { Banner = "warming up", AfterReady = "listening", Stderr = ["note"] };
        var folder = TestPlugins.Folder(_dir, behaviour: behaviour);

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct);

        // Each stream has a reader of its own, so the order of stdout's lines against stderr's is not kept.
        await Eventually.WaitForAsync(
            () => _log.Has("plugin test: warming up") && _log.Has("plugin test: listening") && _log.Has("plugin test: note"), "the three lines", Patience);

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
        Assert.True(TestPlugins.RunsIn(_dir), "the check does not see the running plugin");

        await plugin.StopAsync().WaitAsync(StopWithin, ct);
        await plugin.Exited.WaitAsync(Patience, ct);

        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    /// <summary>The wait after the kill has a bound of its own, so with no grace at all the stop still sees the plugin gone.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AKilledProgramIsWaitedForApartFromTheGrace()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { IgnoreStdin = true });
        var plugin = await StartAsync(TestPlugins.Launch(folder), ct, PluginTimings.Default with { StopGrace = TimeSpan.Zero });

        await plugin.StopAsync().WaitAsync(StopWithin, ct);

        Assert.True(plugin.Exited.IsCompleted, "StopAsync returned before the killed plugin was gone");
    }

    /// <summary>
    /// The program runs in its folder, the folder and the model path reach it whole since every argument goes through
    /// ArgumentList, and the trust record hashes the program there.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AFolderWithSpacesAndCyrillicWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, folderName: "плагін з пробілом");
        var model = TestPlugins.Model(_dir, new TestPluginBehaviour(), "модель з пробілом.json");
        var launch = TestPlugins.Launch(folder, model);
        var trust = new PluginTrustStore(_dir.File("plugin-trust.json"), _log);
        trust.Confirm(launch.PluginId, folder, launch.Executable);

        await using var plugin = await StartAsync(launch, ct);
        var cwd = (await RawInfoAsync(plugin, ct)).GetProperty("cwd").GetString()!;

        Assert.Equal("Test", plugin.Endpoint.Info.Name);
        Assert.Equal("модель з пробілом", plugin.Endpoint.Info.Model?.Id);
        Assert.True(SamePath(folder, cwd), $"the plugin ran in {cwd}");
        Assert.Equal(PluginTrust.Confirmed, trust.Check(launch.PluginId, folder, launch.Executable).Trust);
    }

    /// <summary>
    /// cmd.exe reads a batch file's command line again and acts on these characters, even in quotes for some: a model
    /// path with <c>&amp;</c> would run what follows it. Dictum refuses such a launch before anything runs, so the
    /// launcher leaves no mark.
    /// </summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData("plugin.cmd", "R&D")]
    [InlineData("plugin.BAT", "50%PATH%")]
    [InlineData("plugin.cmd", "x^y!")]
    [InlineData("plugin.cmd.", "a(b)")]
    public async Task ABatchLauncherIsNeverGivenAnArgumentThatCmdActsOn(string launcher, string modelFolder)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows runs a batch file through cmd.exe");
        var ct = TestContext.Current.CancellationToken;
        var folder = BatchLauncherFolder(launcher);
        var model = Directory.CreateDirectory(_dir.File(modelFolder)).FullName;
        var launch = TestPlugins.Launch(folder, model);

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(launch, ct));

        Assert.Equal(
            $"Plugin test starts through {Path.GetFileName(launch.Executable)}, which cannot be given {model}: it holds one of & % ^ ! \" < > | ( ).",
            error.Message);
        Assert.Equal("Use a path without those characters, or ask the plugin's author for a program (.exe) instead of a script.", error.Hint);
        Assert.False(File.Exists(Path.Combine(folder, StartedMark)), "the launcher ran");
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ABatchLauncherStartsWithAnOrdinaryPath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows runs a batch file through cmd.exe");
        var ct = TestContext.Current.CancellationToken;
        var folder = BatchLauncherFolder("plugin.cmd");
        var model = Directory.CreateDirectory(_dir.File("m1")).FullName;

        await using var plugin = await StartAsync(TestPlugins.Launch(folder, model), ct);

        Assert.Equal("m1", plugin.Endpoint.Info.Model?.Id);
        Assert.True(File.Exists(Path.Combine(folder, StartedMark)), "the launcher did not run");
    }

    /// <summary>
    /// .NET reads a redirected stream asynchronously by blocking a thread-pool thread, since the pipe is synchronous: two
    /// for each plugin, which starved the pool of a 2-core machine. Each stream has a thread of its own instead.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task OutputIsReadOffTheThreadPool()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new ThreadNotingLog();
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { Banner = "warming up", Stderr = ["note"] });

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct, log: log);
        await Eventually.WaitForAsync(() => log.PluginLines.Count == 2, "a line of each stream", Patience);

        Assert.All(log.PluginLines, line => Assert.False(line.OnPool, $"'{line.Message}' was handled on a thread-pool thread"));
    }

    /// <summary>Ledger 59: a plugin that answers <c>/health</c>, but never with 200, used to time out without a word on why.</summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData(404, "404 Not Found")]
    [InlineData(500, "500 Internal Server Error")]
    public async Task AStartTimeoutSaysWhatHealthLastAnswered(int status, string answer)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { HealthStatus = status });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct, HealthTimeout));

        Assert.Equal($"Plugin test did not get ready within 5 s; /health last answered {answer}.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AStartTimeoutSaysWhyHealthCouldNotBeReached()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ReadyUrl = $"http://127.0.0.1:{FreePort()}" });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct, HealthTimeout));

        const string Start = "Plugin test did not get ready within 5 s; /health could not be reached: ";
        Assert.StartsWith(Start, error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length > Start.Length + 1, $"no reason in '{error.Message}'");
        Assert.EndsWith(".", error.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AStartTimeoutSaysThatHealthsAnswerCouldNotBeRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { HealthCutShort = true });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct, HealthTimeout));

        const string Start = "Plugin test did not get ready within 5 s; its last answer to /health could not be read: ";
        Assert.StartsWith(Start, error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length > Start.Length + 1, $"no reason in '{error.Message}'");
    }

    /// <summary>Ledger 62: an <c>https</c> address on this computer used to be told it is not on this computer.</summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData("https://127.0.0.1:5001")]
    [InlineData("https://localhost:5001")]
    public async Task AnHttpsReadyUrlIsRefusedAsNotHttp(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ReadyUrl = url });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal($"Plugin test announced {url}, but protocol 1 is plain http on this computer.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    /// <summary>Ledger 60: 401 or 403, to either request of the start.</summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData(401, null)]
    [InlineData(null, 403)]
    public async Task AnUnauthorizedAnswerSaysTheKeyWasRejected(int? healthStatus, int? infoStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { HealthStatus = healthStatus, InfoStatus = infoStatus });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal("Plugin test rejected Dictum's key.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    /// <summary>Ledger 60: the system refuses to run the program, so it never printed anything.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AProgramTheSystemCannotStartFailsWithItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir);
        var launch = TestPlugins.Launch(folder) with { Executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "missing.exe" : "missing") };

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(launch, ct));

        const string Start = "Plugin test could not be started: ";
        Assert.StartsWith(Start, error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length > Start.Length + 1, $"no reason in '{error.Message}'");
        Assert.Equal(Hint, error.Hint);
        Assert.Empty(error.Output);
    }

    /// <summary>Ledger 60: <c>/dictum/v1/info</c> answers, but not as protocol 1 asks.</summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData(500, null, "Plugin test answered 500 to /dictum/v1/info.")]
    [InlineData(null, "not json", "Plugin test answered /dictum/v1/info with invalid JSON: ")]
    [InlineData(null, "null", "Plugin test answered /dictum/v1/info with invalid JSON: it holds no object.")]
    public async Task AnInfoFailureIsReported(int? status, string? body, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { InfoStatus = status, InfoBody = body });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    /// <summary>
    /// The plugin's last lines are still in its pipes when it exits, and the start's failure reports them: here the
    /// last of a flood that fills the pipe, written right before the exit.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnExitWhileStartingReportsItsVeryLastLines()
    {
        var ct = TestContext.Current.CancellationToken;
        var lines = Enumerable.Range(1, 20_000).Select(i => $"err {i}").ToArray();
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { Stderr = lines, ExitAtStart = 3 });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal("Plugin test exited with code 3 while starting.", error.Message);
        Assert.Equal(lines[^20..], error.Output);
    }

    /// <summary>
    /// A process the plugin started that follows protocol 1 exits once its input closes, and Dictum closes it as soon as
    /// the plugin exits. No group ends the child here, as on a system that has none.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AChildThatFollowsTheProtocolEndsWhenThePluginExits()
    {
        var ct = TestContext.Current.CancellationToken;
        var behaviour = new TestPluginBehaviour { ExitAfterReadyMs = CrashAfterMs, LeaveChild = true, ChildFollowsStdin = true };
        var folder = TestPlugins.Folder(_dir, behaviour: behaviour);

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct, groups: NullProcessGroups.Instance);
        using var child = await TestPlugins.LeftChildAsync(folder, Patience);

        if (child is not null)
        {
            await child.WaitForExitAsync(ct).WaitAsync(Patience, ct);
        }

        Assert.True(plugin.Exited.IsCompleted, "the plugin's exit was not noticed");
    }

    /// <summary>
    /// Where no group ends what the plugin started, a process that holds its output after it exited gets one warning,
    /// which names the plugin's pid. The exit itself is noticed at once.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AProcessLeftRunningAfterTheExitIsWarnedOfOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ExitAfterReadyMs = CrashAfterMs, LeaveChild = true });

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct, groups: NullProcessGroups.Instance);
        using var child = await TestPlugins.LeftChildAsync(folder, Patience);
        Assert.NotNull(child);
        await plugin.Exited.WaitAsync(Patience, ct);
        var warning = $"Plugin test (pid {plugin.Id}) exited 5 s ago, but its output is still open: a process it started is still running";
        await Eventually.WaitForAsync(() => _log.Has(warning), "the warning", Patience);

        Assert.Single(_log.Lines, line => line == (LogLevel.Warning, warning));
        Assert.False(child.HasExited, "the child ended, so nothing held the output");
    }

    /// <summary>
    /// A stop waits a little for the output to close before Dictum lets go of the plugin, so what still arrives soon
    /// after the exit reaches the log: here a line that a process the plugin left prints half a second after it started.
    /// Ledger 61: the stop does not take the plugin, which has exited, for one that would not exit.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task WhatArrivesSoonAfterTheExitStillGoesToTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var behaviour = new TestPluginBehaviour { ExitAfterReadyMs = CrashAfterMs, LeaveChild = true, ChildSaysAfterMs = 500 };
        var folder = TestPlugins.Folder(_dir, behaviour: behaviour);

        var plugin = await StartAsync(TestPlugins.Launch(folder), ct, groups: NullProcessGroups.Instance);
        await plugin.Exited.WaitAsync(Patience, ct);
        await plugin.StopAsync().WaitAsync(StopWithin, ct);

        Assert.True(_log.Has("plugin test: child here"), "the line that arrived after the exit did not reach the log");
        Assert.DoesNotContain(_log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("did not exit", StringComparison.Ordinal));
    }

    /// <summary>
    /// The test plugin started through a batch file named <paramref name="launcher"/>, as a plugin in a scripting
    /// language often is. The batch file writes <see cref="StartedMark"/> before it starts the program.
    /// </summary>
    private string BatchLauncherFolder(string launcher)
    {
        var folder = TestPlugins.Folder(_dir);
        File.WriteAllText(
            Path.Combine(folder, launcher),
            $"@echo started> \"%~dp0{StartedMark}\"\r\n@\"%~dp0dictum-test-plugin.exe\" %*\r\n");
        var manifestFile = Path.Combine(folder, PluginProtocol.ManifestFile);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestFile))!;
        manifest["executable"]![PluginProtocol.CurrentRid] = launcher;
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        return folder;
    }

    private Task<PluginProcess> StartAsync(
        PluginLaunch launch, CancellationToken ct, PluginTimings? timings = null, IProcessGroups? groups = null, ILog? log = null) =>
        PluginProcess.StartAsync(launch, _http, groups ?? PlatformFactory.CreateProcessGroups(), log ?? _log, timings ?? PluginTimings.Default, ct);

    /// <summary>A port on this computer that nothing listens on, for a moment at least.</summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Starts the plugin, reads what <c>/dictum/v1/info</c> says of its arguments, and stops it.</summary>
    private async Task<(string[] Argv, int? Threads, string Key)> InfoAsync(PluginLaunch launch, CancellationToken ct)
    {
        await using var plugin = await StartAsync(launch, ct);
        var info = await RawInfoAsync(plugin, ct);
        var argv = info.GetProperty("argv").EnumerateArray().Select(argument => argument.GetString()!).ToArray();
        var threads = info.GetProperty("threads") is { ValueKind: JsonValueKind.Number } number ? number.GetInt32() : (int?)null;
        return (argv, threads, plugin.Endpoint.Key);
    }

    /// <summary>Asks <c>/dictum/v1/info</c> directly with the endpoint's key, for the test plugin's extra fields.</summary>
    private async Task<JsonElement> RawInfoAsync(PluginProcess plugin, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(plugin.Endpoint.Url, "/dictum/v1/info"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plugin.Endpoint.Key);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return info.RootElement.Clone();
    }

    private static bool SamePath(string expected, string actual) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            PathComparison.Current);

    /// <summary>Notes, for each line of the plugin's output that reaches the log, whether a thread-pool thread wrote it.</summary>
    private sealed class ThreadNotingLog : ILog
    {
        public ConcurrentQueue<(string Message, bool OnPool)> PluginLines { get; } = new();

        public bool IncludeText => false;

        public void Write(LogLevel level, string message, Exception? exception = null)
        {
            if (message.StartsWith("plugin test: ", StringComparison.Ordinal))
            {
                PluginLines.Enqueue((message, Thread.CurrentThread.IsThreadPoolThread));
            }
        }
    }
}
