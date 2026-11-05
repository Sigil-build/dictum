using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Dictum.Core;
using Dictum.Core.Diagnostics;
using Dictum.Plugins;
using Dictum.Testing;
using Dictum.TestPlugin;

namespace Dictum.Host.Tests.Plugins;

/// <summary>One plugin process under protocol 1, played by copies of the test plugin.</summary>
public sealed class PluginProcessTests : IDisposable
{
    private const string Hint = "See the plugin's output, if it printed any, and its own documentation.";

    /// <summary>How long a crashing plugin lives after its info answer, so that the crash never lands inside the start.</summary>
    private const int CrashAfterMs = 500;

    /// <summary>For waits on the plugin's output, which a busy runner may deliver late.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>A stop that kills: the grace, up to 5 s for the killed plugin to be gone, and room for a busy runner.</summary>
    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A start that is meant to time out after the ready line. It counts from the launch, so it has to cover the test
    /// plugin's own start on a busy 2-core runner as well as Windows, which tries a refused connection on this computer
    /// again for about 2 s before it gives up on one.
    /// </summary>
    private static readonly PluginTimings HealthTimeout = PluginTimings.Default with { StartTimeout = TimeSpan.FromSeconds(10) };

    /// <summary>How long <see cref="SlowLog"/> takes over its line: well inside the 2 s that Dictum gives a plugin's last lines.</summary>
    private static readonly TimeSpan SlowLine = TimeSpan.FromSeconds(1);

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

    /// <summary>Review focus 4: an address the ready line announces is the plugin's text, and the refusal quotes it spelt out.</summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData("http://192.0.2.1:80/\u001b]0;PWNED\u0007", "which is not on this computer.")]
    [InlineData("https://127.0.0.1:1/\u001b]0;PWNED\u0007", "but protocol 1 is plain http on this computer.")]
    public async Task ARefusedAddressIsQuotedSpeltOut(string url, string why)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ReadyUrl = url });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.Equal($@"Plugin test announced {url[..url.IndexOf('\u001b', StringComparison.Ordinal)]}\u001b]0;PWNED\u0007, {why}", error.Message);
    }

    /// <summary>The address the plugin is ready at reaches the log, which a foreground daemon prints, spelt out.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task TheReadyAddressIsLoggedSpeltOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ReadyPath = "/\u001b]0;PWNED\u0007" });

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct);

        Assert.True(_log.Has($@"Plugin test is ready at http://127.0.0.1:{plugin.Endpoint.Url.Port}/\u001b]0;PWNED\u0007 (pid {plugin.Id})"), string.Join('\n', _log.Messages));
        Assert.DoesNotContain(_log.Messages, message => message.Contains('\u001b', StringComparison.Ordinal));
    }

    /// <summary>
    /// Every line of the plugin reaches the log spelt out, since a foreground daemon and <c>--verbose</c> print the log on a
    /// terminal; the lines a failure quotes stay as the plugin printed them, and each report spells them out itself.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ThePluginsLinesAreLoggedSpeltOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ExitAtStart = 3, Stderr = ["a\u001b]0;PWNED\u0007b"] });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.True(_log.Has(@"plugin test: a\u001b]0;PWNED\u0007b"), string.Join('\n', _log.Messages));
        Assert.DoesNotContain(_log.Messages, message => message.Contains('\u001b', StringComparison.Ordinal));
        Assert.Equal(["a\u001b]0;PWNED\u0007b"], error.Output);
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
    /// The plugin runs in its folder, which only a listed file is checked in. So no program there runs by its bare name,
    /// as cmd.exe would run a planted <c>python.cmd</c> before the Python on <c>PATH</c>, and Python keeps its compiled
    /// modules in Dictum's folder for the plugin rather than in a <c>__pycache__</c> beside them, where a planted one
    /// would run in place of a checked module.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task DictumsOwnVariablesReachThePlugin()
    {
        var ct = TestContext.Current.CancellationToken;
        var launch = TestPlugins.Launch(TestPlugins.Folder(_dir));

        await using var plugin = await StartAsync(launch, ct);
        var environment = (await RawInfoAsync(plugin, ct)).GetProperty("env");

        Assert.Equal("1", environment.GetProperty("NoDefaultCurrentDirectoryInExePath").GetString());
        Assert.Equal(launch.Cache, environment.GetProperty("PYTHONPYCACHEPREFIX").GetString());
        Assert.False(string.IsNullOrEmpty(environment.GetProperty(PluginProtocol.KeyVariable).GetString()));
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
        var folder = TestPlugins.BatchLauncherFolder(_dir, launcher);
        var model = Directory.CreateDirectory(_dir.File(modelFolder)).FullName;
        var launch = TestPlugins.Launch(folder, model);

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(launch, ct));

        Assert.Equal(
            $"Plugin test starts through {Path.GetFileName(launch.Executable)}, which cannot be given {model}: it holds one of & % ^ ! \" < > | ( ).",
            error.Message);
        Assert.Equal("Use a path without those characters, or ask the plugin's author for a program (.exe) instead of a script.", error.Hint);
        Assert.False(File.Exists(Path.Combine(folder, TestPlugins.StartedMark)), "the launcher ran");
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin runs");
    }

    /// <summary>Only a batch file is refused such characters; a program (.exe) is given its arguments as they are.</summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData("R&D")]
    [InlineData("50%PATH%")]
    public async Task AnExeLauncherStillGetsAmpersandAndPercentInItsArgumentsUnchanged(string modelFolder)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir);
        Directory.CreateDirectory(_dir.File(modelFolder));
        var model = TestPlugins.Model(_dir, new TestPluginBehaviour(), Path.Combine(modelFolder, "model.json"));

        var (argv, _, _) = await InfoAsync(TestPlugins.Launch(folder, model), ct);

        Assert.Equal(model, argv[Array.IndexOf(argv, "--model") + 1]);
    }

    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ABatchLauncherStartsWithAnOrdinaryPath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows runs a batch file through cmd.exe");
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.BatchLauncherFolder(_dir, "plugin.cmd");
        var model = Directory.CreateDirectory(_dir.File("m1")).FullName;

        await using var plugin = await StartAsync(TestPlugins.Launch(folder, model), ct);

        Assert.Equal("m1", plugin.Endpoint.Info.Model?.Id);
        Assert.True(File.Exists(Path.Combine(folder, TestPlugins.StartedMark)), "the launcher did not run");
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
    [Theory(Timeout = TestTimeout.Extended)]
    [InlineData(404, "404 Not Found")]
    [InlineData(500, "500 Internal Server Error")]
    public async Task AStartTimeoutSaysWhatHealthLastAnswered(int status, string answer)
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { HealthStatus = status });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct, HealthTimeout));

        Assert.Equal($"Plugin test did not get ready within 10 s; /health last answered {answer}.", error.Message);
        Assert.Equal(Hint, error.Hint);
        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    [Fact(Timeout = TestTimeout.Extended)]
    public async Task AStartTimeoutSaysWhyHealthCouldNotBeReached()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ReadyUrl = $"http://127.0.0.1:{FreePort()}" });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct, HealthTimeout));

        const string Start = "Plugin test did not get ready within 10 s; /health could not be reached: ";
        Assert.StartsWith(Start, error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length > Start.Length + 1, $"no reason in '{error.Message}'");
        Assert.EndsWith(".", error.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeout.Extended)]
    public async Task AStartTimeoutSaysThatHealthsAnswerCouldNotBeRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { HealthCutShort = true });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct, HealthTimeout));

        const string Start = "Plugin test did not get ready within 10 s; its last answer to /health could not be read: ";
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
    /// System.Text.Json puts a property name from the plugin's answer into its exception's path. The name holds an ESC,
    /// which the failure prints spelt out, not raw, as it prints every other text from a plugin.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnInfoReaderMessageHoldingAPluginsBytesIsSpeltOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { InfoBody = "{\"\\u001bq\": {\"a\": [1,}}" });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(() => StartAsync(TestPlugins.Launch(folder), ct));

        Assert.StartsWith("Plugin test answered /dictum/v1/info with invalid JSON: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("\\u001bq", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', error.Message);
    }

    /// <summary>
    /// The plugin's last lines can still be in its pipes when it exits, and the start's failure reports them. The log is
    /// slow on the first line here, as on a busy machine, so both lines are still unhandled when the exit is noticed.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AnExitWhileStartingReportsItsVeryLastLines()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { Stderr = ["slow", "last"], ExitAtStart = 3 });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(
            () => StartAsync(TestPlugins.Launch(folder), ct, log: new SlowLog(_log, "plugin test: slow")));

        Assert.Equal("Plugin test exited with code 3 while starting.", error.Message);
        Assert.Equal(["slow", "last"], error.Output);
    }

    /// <summary>The exit is logged after the plugin's last lines, so that dictum.log shows the cause above the exit.</summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task TheExitIsLoggedAfterTheLastLines()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { Stderr = ["slow", "last"], ExitAtStart = 3 });

        await Assert.ThrowsAsync<PluginUnavailableException>(
            () => StartAsync(TestPlugins.Launch(folder), ct, log: new SlowLog(_log, "plugin test: slow")));

        var messages = _log.Messages.ToList();
        var last = messages.IndexOf("plugin test: last");
        var exit = messages.FindIndex(
            message => message.StartsWith("Plugin test (pid ", StringComparison.Ordinal) && message.EndsWith(") exited with code 3", StringComparison.Ordinal));
        Assert.True(last >= 0, "the last line is not in the log");
        Assert.True(exit > last, $"the exit is logged at {exit}, before the last line at {last}");
    }

    /// <summary>
    /// Lines still in the pipe when a start fails reach the log before Dictum lets go of the plugin. The log is slow on
    /// the first line here, so the second is still unhandled when the plugin, which speaks another protocol, is killed.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task WhatIsLeftInThePipeWhenAStartFailsStillGoesToTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { Stderr = ["slow", "last"], InfoProtocol = 2 });

        var error = await Assert.ThrowsAsync<PluginUnavailableException>(
            () => StartAsync(TestPlugins.Launch(folder), ct, log: new SlowLog(_log, "plugin test: slow")));

        Assert.Equal("Plugin test speaks protocol 2; this Dictum speaks protocol 1.", error.Message);
        Assert.True(_log.Has("plugin test: last"), "the line still in the pipe did not reach the log");
    }

    /// <summary>
    /// A line that the log cannot take ends neither Dictum nor the reading of either stream: the failure is logged
    /// once, and the lines after it, the ready line among them, are handled as usual.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task ALineThatCannotBeHandledStopsNeitherDictumNorTheOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var behaviour = new TestPluginBehaviour { Stderr = ["boom", "note"], Banner = "boom", AfterReady = "listening" };
        var folder = TestPlugins.Folder(_dir, behaviour: behaviour);

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct, log: new ThrowingLog(_log, "plugin test: boom"));
        await Eventually.WaitForAsync(() => _log.Has("plugin test: note") && _log.Has("plugin test: listening"), "the lines after the failed ones", Patience);

        var warning = Assert.Single(_log.Lines, line => line.Level == LogLevel.Warning);
        Assert.Matches(
            $@"^Plugin test \(pid {plugin.Id}\): a line on its std(out|err) could not be handled, so it is left out of the log: {ThrowingLog.Failure}$",
            warning.Message);
    }

    /// <summary>
    /// A system that will not put the plugin in a group does not keep it from running, nor does a group that fails in a
    /// way nobody planned for, which would otherwise leave the started process running with nothing reading its output:
    /// the log says so, naming the pid, and the stop still ends the plugin.
    /// </summary>
    [Theory(Timeout = TestTimeout.Generous)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APluginTheSystemWillNotGroupStillRuns(bool unplanned)
    {
        var ct = TestContext.Current.CancellationToken;
        Exception refusal = unplanned ? new InvalidOperationException("The group broke.") : new Win32Exception(5);

        var plugin = await StartAsync(TestPlugins.Launch(TestPlugins.Folder(_dir)), ct, groups: new RefusingGroups(refusal));
        var warning = $"Plugin test (pid {plugin.Id}) runs outside a process group, so a process it starts may outlive it: {refusal.Message}";
        Assert.Equal("Test", plugin.Endpoint.Info.Name);
        Assert.Single(_log.Lines, line => line == (LogLevel.Warning, warning));

        await plugin.StopAsync().WaitAsync(StopWithin, ct);

        Assert.False(TestPlugins.RunsIn(_dir), "the plugin still runs");
    }

    /// <summary>
    /// A process the plugin started that follows protocol 1 exits once its input closes, and Dictum closes it as soon as
    /// the plugin exits. No group ends the child here, as on a system that has none.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AChildThatFollowsTheProtocolEndsWhenThePluginExits()
    {
        var ct = TestContext.Current.CancellationToken;
        var behaviour = new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs, LeaveChild = true, ChildFollowsStdin = true };
        var folder = TestPlugins.Folder(_dir, behaviour: behaviour);

        await using var plugin = await StartAsync(TestPlugins.Launch(folder), ct, groups: NullProcessGroups.Instance);
        using var child = await TestPlugins.LeftChildAsync(folder, Patience);

        if (child is not null)
        {
            await child.WaitForExitAsync(ct).WaitAsync(Patience, ct);
        }

        // The child goes once Dictum closes its stdin, which follows the noticed exit; waiting keeps a slow runner honest.
        await plugin.Exited.WaitAsync(Patience, ct);
        Assert.True(child is null || child.HasExited, "the child still runs");
    }

    /// <summary>
    /// Nothing on a reader's thread may end Dictum. A stream that fails, in a way no broken pipe does, is reported once
    /// and still counts as closed, or the wait for the plugin's last lines would last its full time at every stop.
    /// </summary>
    [Fact]
    public void AReaderWhoseStreamFailsReportsItAndStillCountsAsClosed()
    {
        var failure = new InvalidOperationException("the stream broke");
        var reports = new List<string>();
        var closed = 0;
        var lines = new List<string>();
        var stream = new FailingReader(failure, "one line");

        PluginProcess.ReadLines(stream, "stdout", lines.Add, (what, ex) => reports.Add($"{what}: {ex.Message}"), () => closed++);

        Assert.Equal(["one line"], lines);
        Assert.Equal(["its stdout could not be read any further: the stream broke"], reports);
        Assert.Equal(1, closed);
        Assert.True(stream.Disposed);
    }

    /// <summary>The pipe breaking is the end of the output, which needs no report.</summary>
    [Fact]
    public void AReaderWhosePipeBreaksReportsNothingAndCountsAsClosed()
    {
        var reports = new List<string>();
        var closed = 0;

        PluginProcess.ReadLines(new FailingReader(new IOException("broken pipe")), "stderr", _ => { }, (what, _) => reports.Add(what), () => closed++);

        Assert.Empty(reports);
        Assert.Equal(1, closed);
    }

    /// <summary>One line the log cannot take is reported, and the lines after it are still read, so the plugin never blocks.</summary>
    [Fact]
    public void ALineThatCannotBeHandledLeavesTheRestToBeRead()
    {
        var reports = new List<string>();
        var lines = new List<string>();

        PluginProcess.ReadLines(
            new StringReader("first\nsecond"),
            "stdout",
            line =>
            {
                lines.Add(line);
                if (line == "first")
                {
                    throw new InvalidOperationException("the log is full");
                }
            },
            (what, ex) => reports.Add($"{what}: {ex.Message}"),
            () => { });

        Assert.Equal(["first", "second"], lines);
        Assert.Equal(["a line on its stdout could not be handled, so it is left out of the log: the log is full"], reports);
    }

    private sealed class FailingReader(Exception failure, params string[] lines) : TextReader
    {
        private readonly Queue<string> _lines = new(lines);

        public bool Disposed { get; private set; }

        public override string? ReadLine() => _lines.TryDequeue(out var line) ? line : throw failure;

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Where no group ends what the plugin started, a process that holds its output after it exited gets one warning,
    /// which names the plugin's pid. The exit itself is noticed at once.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task AProcessLeftRunningAfterTheExitIsWarnedOfOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs, LeaveChild = true });

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
    /// after the exit reaches the log: here a line that a process the plugin left prints a moment after it started.
    /// Ledger 61: the stop does not take the plugin, which has exited, for one that would not exit.
    /// </summary>
    [Fact(Timeout = TestTimeout.Generous)]
    public async Task WhatArrivesSoonAfterTheExitStillGoesToTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var behaviour = new TestPluginBehaviour { CrashAfterInfoMs = CrashAfterMs, LeaveChild = true, ChildSaysAfterMs = 100 };
        var folder = TestPlugins.Folder(_dir, behaviour: behaviour);

        var plugin = await StartAsync(TestPlugins.Launch(folder), ct, groups: NullProcessGroups.Instance);
        await plugin.Exited.WaitAsync(Patience, ct);
        await plugin.StopAsync().WaitAsync(StopWithin, ct);

        Assert.True(_log.Has("plugin test: child here"), "the line that arrived after the exit did not reach the log");
        Assert.DoesNotContain(_log.Lines, line => line.Level == LogLevel.Warning && line.Message.Contains("did not exit", StringComparison.Ordinal));
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

    /// <summary>Takes <see cref="SlowLine"/> over one line, as a log on a busy machine might, holding up the stream's reader.</summary>
    private sealed class SlowLog(ListLog inner, string slowMessage) : ILog
    {
        public bool IncludeText => false;

        public void Write(LogLevel level, string message, Exception? exception = null)
        {
            if (message == slowMessage)
            {
                Thread.Sleep(SlowLine);
            }

            inner.Write(level, message, exception);
        }
    }

    /// <summary>Throws on one line, and keeps every other one.</summary>
    private sealed class ThrowingLog(ListLog inner, string failingMessage) : ILog
    {
        public const string Failure = "the log cannot take this line";

        public bool IncludeText => false;

        public void Write(LogLevel level, string message, Exception? exception = null)
        {
            if (message == failingMessage)
            {
                throw new InvalidOperationException(Failure);
            }

            inner.Write(level, message, exception);
        }
    }

    /// <summary>A system that will not put a process in a group, with <paramref name="refusal"/>, such as ERROR_ACCESS_DENIED.</summary>
    private sealed class RefusingGroups(Exception refusal) : IProcessGroups
    {
        public IDisposable Enclose(Process process) => throw refusal;
    }

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
