using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dictum.Core;
using Dictum.Core.Diagnostics;
using Dictum.Core.Text;

namespace Dictum.Plugins;

/// <summary>
/// One running plugin program under protocol 1. It gets a fresh key in its environment, announces its address in a
/// ready line on stdout, and is ready once <c>/health</c> answers 200 and <c>/dictum/v1/info</c> names protocol 1.
/// Both of its output streams are read until they close, each on a thread of its own, so a talkative plugin never
/// blocks on a full pipe, and no plugin holds a thread-pool thread.
/// Its stdin is a pipe that only Dictum holds: <see cref="StopAsync"/> closes it, the plugin's sign to exit, and kills
/// the process tree after the grace. If Dictum itself dies, the pipe closes with it.
/// It starts in a group of its own, which every process it starts joins (<see cref="IProcessGroups"/>). Once it exits,
/// by itself or not, Dictum closes its stdin, so that a process it started that follows protocol 1 exits too, and ends
/// the group, which kills whatever else of it is left.
/// </summary>
public sealed class PluginProcess : IAsyncDisposable
{
    private const int KeyBytes = 32;
    private const int KeptLines = 20;
    private const string HealthPath = "/health";
    private const string InfoPath = "/dictum/v1/info";
    private const string Hint = "See the plugin's output, if it printed any, and its own documentation.";

    /// <summary>Set, it keeps Windows from running a program in the working folder by its bare name.</summary>
    private const string NoCurrentDirectoryVariable = "NoDefaultCurrentDirectoryInExePath";

    /// <summary>Where Python 3.8 and later write and read their compiled modules, in place of <c>__pycache__</c> folders.</summary>
    private const string PythonCacheVariable = "PYTHONPYCACHEPREFIX";

    /// <summary>
    /// How long a killed plugin has to be gone. Apart from the grace, which is the plugin's time to exit by itself and
    /// may be short.
    /// </summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the last lines of a plugin that exited get to arrive. Its own lines are in its pipes when it exits, so
    /// they arrive at once; only a process it left running, which holds the pipes open, makes this wait last.
    /// </summary>
    private static readonly TimeSpan LastLinesWait = TimeSpan.FromSeconds(2);

    /// <summary>How long the output may stay open after the exit before a warning says that a process it started holds it.</summary>
    private static readonly TimeSpan LeftRunningWait = TimeSpan.FromSeconds(5);

    /// <summary>What cmd.exe acts on in a batch file's command line.</summary>
    private static readonly SearchValues<char> BatchSpecial = SearchValues.Create("&%^!\"<>|()");

    private readonly PluginLaunch _launch;
    private readonly ILog _log;
    private readonly TimeSpan _stopGrace;
    private readonly Process _process;
    private readonly IDisposable _group;
    private readonly Lock _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly TaskCompletionSource<PluginReadyLine> _readyLine = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once both output streams have closed, which a process the plugin started may put off.</summary>
    private readonly TaskCompletionSource _outputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cancelled once the process has ended, so that every wait of the start ends with it.</summary>
    private readonly CancellationTokenSource _ended = new();

    /// <summary>One stop, however many callers ask for it.</summary>
    private readonly Lazy<Task> _stopping;

    private int _openStreams = 2;
    private int _inputClosed;
    private int _readerFailureReported;

    /// <summary>
    /// Set at the exit: completes once the last lines have arrived, or <see cref="LastLinesWait"/> after the exit, and
    /// the exit is logged after them. One wait, however many callers ask for it.
    /// </summary>
    private Task _lastLines = Task.CompletedTask;

    /// <summary>Set once Dictum lets go of the plugin: lines that still arrive no longer go to the log.</summary>
    private volatile bool _released;

    /// <summary>
    /// What the start last heard at the plugin's address, for a start that times out: null before the ready line, then
    /// what <c>/health</c> last answered, then that <c>/dictum/v1/info</c> has not answered yet.
    /// </summary>
    private string? _lastHeard;

    /// <exception cref="PluginUnavailableException">The system refused to start the program.</exception>
    private PluginProcess(PluginLaunch launch, IReadOnlyList<string> arguments, string key, IProcessGroups groups, ILog log, TimeSpan stopGrace)
    {
        _launch = launch;
        _log = log;
        _stopGrace = stopGrace;
        _stopping = new Lazy<Task>(StopCoreAsync);
        var start = StartInfo(launch, arguments, key);
        groups.Prepare(start);
        _process = new Process { StartInfo = start };
        try
        {
            _process.Start();
        }
        catch (Win32Exception ex)
        {
            _process.Dispose();
            throw new PluginUnavailableException($"Plugin {launch.PluginId} could not be started: {TerminalText.Show(ex.Message)}", Hint);
        }

        Id = _process.Id;

        // At once, so that every process the plugin starts joins the group.
        _group = Enclose(groups);
        Read(_process.StandardOutput, fromStdout: true);
        Read(_process.StandardError, fromStdout: false);
        Exited = WatchExitAsync();
    }

    public int Id { get; }

    /// <summary>Set before <see cref="StartAsync"/> returns.</summary>
    public PluginEndpoint Endpoint { get; private set; } = null!;

    /// <summary>
    /// Completes with the exit code once the process has ended, whether or not a process it started still holds its
    /// output open; its last lines may still be on their way.
    /// </summary>
    public Task<int> Exited { get; }

    /// <summary>The last 20 lines of either stream, oldest first; the ready line is not among them.</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (_gate)
            {
                return [.. _recent];
            }
        }
    }

    /// <summary>
    /// Launches the plugin and waits until it is ready, within <see cref="PluginTimings.StartTimeout"/>. On any failure
    /// it kills what it launched first; cancelling <paramref name="ct"/> does too, and throws as usual.
    /// </summary>
    /// <exception cref="PluginUnavailableException">
    /// It could not start, exited, timed out or broke protocol 1; or, before anything runs, its batch file would have
    /// been given an argument that cmd.exe acts on.
    /// </exception>
    public static async Task<PluginProcess> StartAsync(
        PluginLaunch launch, HttpClient http, IProcessGroups groups, ILog log, PluginTimings timings, CancellationToken ct)
    {
        RefuseWhatABatchFileWouldRun(launch);
        var arguments = Arguments(launch);
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(KeyBytes));
        var plugin = new PluginProcess(launch, arguments, key, groups, log, timings.StopGrace);
        try
        {
            using var start = CancellationTokenSource.CreateLinkedTokenSource(ct, plugin._ended.Token);
            start.CancelAfter(timings.StartTimeout);
            try
            {
                plugin.Endpoint = await plugin.ConnectAsync(http, key, timings.HealthPoll, start.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (!plugin._ended.IsCancellationRequested)
                {
                    // Nothing more when it printed no ready line; else what /health last answered, or that info did not.
                    var heard = plugin._lastHeard is { } last ? $"; {(last.EndsWith('.') ? last[..^1] : last)}" : "";
                    throw plugin.Failure($"Plugin {launch.PluginId} did not get ready within {Seconds(timings.StartTimeout)} s{heard}.");
                }

                var code = await plugin.Exited.ConfigureAwait(false);
                await plugin.WaitForLastLinesAsync().ConfigureAwait(false);
                throw plugin.Failure($"Plugin {launch.PluginId} exited with code {code} while starting.");
            }
        }
        catch
        {
            await plugin.KillAsync().ConfigureAwait(false);

            // What it printed until it was killed goes to the log before Dictum lets go of it.
            if (plugin.Exited.IsCompleted)
            {
                await plugin.WaitForLastLinesAsync().ConfigureAwait(false);
            }

            plugin.Release();
            throw;
        }

        log.Info($"Plugin {launch.PluginId} is ready at {TerminalText.Show(plugin.Endpoint.Url.ToString())} (pid {plugin.Id})");
        return plugin;
    }

    /// <summary>
    /// Closes the plugin's stdin, kills its process tree if it has not exited within the grace, and lets its last lines
    /// reach the log before it lets go of it.
    /// </summary>
    public Task StopAsync() => _stopping.Value;

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Once <see cref="Exited"/> has completed: waits until its output has closed, for <see cref="LastLinesWait"/> at
    /// most, so that <see cref="RecentOutput"/> and the log hold the last lines it printed, and the exit is logged.
    /// </summary>
    internal Task WaitForLastLinesAsync() => _lastLines;

    /// <summary>Protocol 1's command line: <c>--port 0 [--model &lt;path&gt;] [--threads &lt;n&gt;]</c>.</summary>
    private static List<string> Arguments(PluginLaunch launch)
    {
        List<string> arguments = ["--port", "0"];
        if (launch.Model is { } model)
        {
            arguments.AddRange(["--model", model]);
        }

        if (launch.Threads is { } threads)
        {
            arguments.AddRange(["--threads", threads.ToString(CultureInfo.InvariantCulture)]);
        }

        return arguments;
    }

    /// <summary>
    /// On Windows, a <c>.cmd</c> or <c>.bat</c> program runs under cmd.exe, which reads the whole command line again:
    /// <c>&amp;</c>, <c>|</c> and the rest run or redirect what follows them, and <c>%</c> expands even inside quotes.
    /// A model path from a copied config must never make Dictum run something the user did not confirm, so such an
    /// argument is refused before anything starts. <see cref="StartAsync"/> checks it first; a caller that counts what
    /// runs checks it before it counts.
    /// </summary>
    /// <exception cref="PluginUnavailableException">An argument holds one of <see cref="BatchSpecial"/>.</exception>
    internal static void RefuseWhatABatchFileWouldRun(PluginLaunch launch)
    {
        if (!OperatingSystem.IsWindows() || !IsBatchFile(launch.Executable)
            || Arguments(launch).Find(argument => argument.AsSpan().ContainsAny(BatchSpecial)) is not { } refused)
        {
            return;
        }

        throw new PluginUnavailableException(
            $"Plugin {launch.PluginId} starts through {Path.GetFileName(launch.Executable)}, which cannot be given {TerminalText.Show(refused)}: it holds one of & % ^ ! \" < > | ( ).",
            "Use a path without those characters, or ask the plugin's author for a program (.exe) instead of a script.");
    }

    /// <summary>By its extension, as Windows decides; a trailing dot or space, which Windows drops from a name, changes nothing.</summary>
    private static bool IsBatchFile(string executable) =>
        Path.GetExtension(executable.TrimEnd('.', ' ')) is var extension
        && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));

    private static ProcessStartInfo StartInfo(PluginLaunch launch, IReadOnlyList<string> arguments, string key)
    {
        var info = new ProcessStartInfo(launch.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = launch.Folder,

            // A pipe of its own, never Dictum's stdin, so that only Dictum can end it.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Each argument whole, whatever spaces or letters the paths hold.
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment[PluginProtocol.KeyVariable] = key;

        // The plugin runs in its folder, of which the trust record checks only what plugin.json lists. On Windows, cmd.exe
        // and the system would search that working folder for a program named by its bare name ahead of PATH, such as a
        // planted python.cmd; Dictum passes full paths, so nothing needs that. This does not cover a native plugin that
        // starts a child by bare name: CreateProcess still tries the caller's own folder first, which listing "." covers.
        // And Python would read a compiled module from a __pycache__ beside its source, which the record skips, so its
        // caches go to Dictum's folder for the plugin.
        info.Environment[NoCurrentDirectoryVariable] = "1";
        info.Environment[PythonCacheVariable] = launch.Cache;
        return info;
    }

    private async Task<PluginEndpoint> ConnectAsync(HttpClient http, string key, TimeSpan healthPoll, CancellationToken ct)
    {
        var ready = await _readyLine.Task.WaitAsync(ct).ConfigureAwait(false);
        if (ready.DictumPlugin != PluginProtocol.Version)
        {
            throw WrongProtocol(ready.DictumPlugin ?? 0);
        }

        if (!Uri.TryCreate(ready.Url, UriKind.Absolute, out var url) || !IsOnThisComputer(url))
        {
            throw Failure($"Plugin {_launch.PluginId} announced {TerminalText.Show(ready.Url)}, which is not on this computer.");
        }

        if (url.Scheme != Uri.UriSchemeHttp)
        {
            throw Failure($"Plugin {_launch.PluginId} announced {TerminalText.Show(ready.Url)}, but protocol 1 is plain http on this computer.");
        }

        _lastHeard = $"{HealthPath} did not answer";
        await WaitUntilHealthyAsync(http, url, key, healthPoll, ct).ConfigureAwait(false);
        _lastHeard = $"{InfoPath} did not answer";
        var info = await GetInfoAsync(http, url, key, ct).ConfigureAwait(false);
        if (info.Protocol != PluginProtocol.Version)
        {
            throw WrongProtocol(info.Protocol);
        }

        return new PluginEndpoint(url, key, info);
    }

    private static bool IsOnThisComputer(Uri url) => url.DnsSafeHost is "127.0.0.1" or "::1" or "localhost";

    private async Task WaitUntilHealthyAsync(HttpClient http, Uri url, string key, TimeSpan poll, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                using var response = await GetAsync(http, url, HealthPath, key, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                ThrowIfKeyRejected(response);
                _lastHeard = $"{HealthPath} last answered {(int)response.StatusCode} {TerminalText.Show(response.ReasonPhrase)}".TrimEnd();
            }
            catch (HttpRequestException ex)
            {
                // Not listening yet: a refused connection waits, like a 503 while the model loads. Why is kept, for a
                // start that times out.
                var reason = TerminalText.Show(ex.GetBaseException().Message);
                _lastHeard = ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                    ? $"{HealthPath} could not be reached: {reason}"
                    : $"its last answer to {HealthPath} could not be read: {reason}";
            }

            await Task.Delay(poll, ct).ConfigureAwait(false);
        }
    }

    private async Task<PluginInfo> GetInfoAsync(HttpClient http, Uri url, string key, CancellationToken ct)
    {
        try
        {
            using var response = await GetAsync(http, url, InfoPath, key, ct).ConfigureAwait(false);
            ThrowIfKeyRejected(response);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure($"Plugin {_launch.PluginId} answered {(int)response.StatusCode} to {InfoPath}.");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize(body, PluginJsonContext.Default.PluginInfo)
                ?? throw Failure($"Plugin {_launch.PluginId} answered {InfoPath} with invalid JSON: it holds no object.");
        }
        catch (HttpRequestException ex)
        {
            throw Failure($"Plugin {_launch.PluginId} did not answer {InfoPath}: {TerminalText.Show(ex.Message)}");
        }
        catch (JsonException ex)
        {
            throw Failure($"Plugin {_launch.PluginId} answered {InfoPath} with invalid JSON: {TerminalText.Show(ex.Message)}");
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri url, string path, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private void ThrowIfKeyRejected(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw Failure($"Plugin {_launch.PluginId} rejected Dictum's key.");
        }
    }

    private PluginUnavailableException WrongProtocol(int protocol) =>
        Failure($"Plugin {_launch.PluginId} speaks protocol {protocol}; this Dictum speaks protocol {PluginProtocol.Version}.");

    private PluginUnavailableException Failure(string message) => new(message, Hint, RecentOutput);

    /// <summary>
    /// The system refusing a group is no reason to refuse the plugin: it runs as before, and its stop still kills its
    /// process tree, but a process it starts may outlive a crash. So is any other failure of the group, which would
    /// otherwise leave the process started with nothing reading its output.
    /// </summary>
    private IDisposable Enclose(IProcessGroups groups)
    {
        try
        {
            return groups.Enclose(_process);
        }
        catch (Exception ex)
        {
            if (!_process.HasExited)
            {
                _log.Warn($"Plugin {_launch.PluginId} (pid {Id}) runs outside a process group, so a process it starts may outlive it: {ex.Message}");
            }

            return NullProcessGroups.Instance.Enclose(_process);
        }
    }

    /// <summary>
    /// Reads one stream to its end on a thread of its own. .NET would read it asynchronously by blocking a thread-pool
    /// thread, since the pipe is synchronous; two for each plugin starved the pool of a 2-core machine. The thread is a
    /// background one, so that a process the plugin left running, which holds the pipe, never keeps Dictum from exiting.
    /// </summary>
    private void Read(StreamReader stream, bool fromStdout)
    {
        var name = fromStdout ? "stdout" : "stderr";
        var reader = new Thread(() => ReadLines(stream, name, line => OnLine(line, fromStdout), ReportOnce, OnStreamClosed))
        {
            IsBackground = true,
            Name = $"plugin {_launch.PluginId} {name}",
        };
        reader.Start();
    }

    /// <summary>
    /// A reader thread's whole work: each line to <paramref name="onLine"/> until the stream ends, and whatever goes wrong
    /// to <paramref name="report"/>, since nothing on such a thread may end Dictum.
    /// </summary>
    internal static void ReadLines(TextReader stream, string name, Action<string> onLine, Action<string, Exception> report, Action closed)
    {
        try
        {
            while (stream.ReadLine() is { } line)
            {
                try
                {
                    onLine(line);
                }
                catch (Exception ex)
                {
                    // One line the log could not take: the rest are still read, so the plugin never blocks.
                    report($"a line on its {name} could not be handled, so it is left out of the log", ex);
                }
            }
        }
        catch (IOException)
        {
            // The pipe broke rather than closed: the end of the output all the same.
        }
        catch (Exception ex)
        {
            report($"its {name} could not be read any further", ex);
        }
        finally
        {
            stream.Dispose();
            closed();
        }
    }

    private void OnStreamClosed()
    {
        if (Interlocked.Decrement(ref _openStreams) == 0)
        {
            _outputClosed.TrySetResult();
        }
    }

    /// <summary>The first failure of either reader goes to the log; the log may be what fails, so it may not throw.</summary>
    private void ReportOnce(string what, Exception ex)
    {
        if (Interlocked.Exchange(ref _readerFailureReported, 1) != 0)
        {
            return;
        }

        try
        {
            _log.Warn($"Plugin {_launch.PluginId} (pid {Id}): {what}: {ex.Message}", ex);
        }
        catch (Exception)
        {
            // The log cannot take this either, and there is nowhere else to say it.
        }
    }

    /// <summary>
    /// Every line of either stream goes to the log and the recent output, except the ready line it waits for. The log
    /// gets it spelt out, since a foreground daemon and <c>--verbose</c> print the log on a terminal; the recent output
    /// keeps it as it is, and each report that quotes it spells it out.
    /// </summary>
    private void OnLine(string line, bool fromStdout)
    {
        if (line.Length == 0 || _released)
        {
            return;
        }

        if (fromStdout && !_readyLine.Task.IsCompleted && ParseReadyLine(line) is { } ready)
        {
            _readyLine.TrySetResult(ready);
            return;
        }

        _log.Info($"plugin {_launch.PluginId}: {TerminalText.Show(line)}");
        lock (_gate)
        {
            if (_recent.Count == KeptLines)
            {
                _recent.Dequeue();
            }

            _recent.Enqueue(line);
        }
    }

    private static PluginReadyLine? ParseReadyLine(string line)
    {
        if (!line.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, PluginJsonContext.Default.PluginReadyLine) is { DictumPlugin: not null, Url: not null } ready
                ? ready
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<int> WatchExitAsync()
    {
        // Nothing reads the streams asynchronously, so this waits for the process alone, not for its pipes to close,
        // which a process it started may put off. Nothing cancels this wait: its end is what cancels the others.
        await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var code = _process.ExitCode;

        // What it left running: a process that follows protocol 1 exits as its stdin closes, and the group ends the rest.
        CloseInput();
        _group.Dispose();
        _lastLines = LogExitAfterLastLinesAsync(code);
        _ = WarnIfLeftRunningAsync();
        await _ended.CancelAsync().ConfigureAwait(false);
        return code;
    }

    /// <summary>
    /// One warning when the output stays open after the exit: a process the plugin started holds it, one that its
    /// group did not end, as on a system without groups.
    /// </summary>
    private async Task WarnIfLeftRunningAsync()
    {
        try
        {
            await _outputClosed.Task.WaitAsync(LeftRunningWait, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn(
                $"Plugin {_launch.PluginId} (pid {Id}) exited {Seconds(LeftRunningWait)} s ago, but its output is still open: a process it started is still running");
        }
    }

    /// <summary>Closes the plugin's stdin once: its sign to exit, and that of every process it started that shares it.</summary>
    private void CloseInput()
    {
        if (Interlocked.Exchange(ref _inputClosed, 1) != 0)
        {
            return;
        }

        try
        {
            _process.StandardInput.Dispose();
        }
        catch (IOException)
        {
            // It has gone already, and its end of the pipe with it.
        }
    }

    private async Task StopCoreAsync()
    {
        CloseInput();
        try
        {
            await Exited.WaitAsync(_stopGrace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn($"Plugin {_launch.PluginId} (pid {Id}) did not exit within {Seconds(_stopGrace)} s of its input closing; ending it");
            await KillAsync().ConfigureAwait(false);
        }

        // What it printed as it exited goes to the log before Dictum lets go of it.
        if (Exited.IsCompleted)
        {
            await WaitForLastLinesAsync().ConfigureAwait(false);
        }

        Release();
    }

    /// <summary>After the last lines, so that the log shows what the plugin said before it says that it exited.</summary>
    private async Task LogExitAfterLastLinesAsync(int code)
    {
        try
        {
            await _outputClosed.Task.WaitAsync(LastLinesWait, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A process it left running holds the pipes open; its own lines have arrived by now.
        }

        _log.Info($"Plugin {_launch.PluginId} (pid {Id}) exited with code {code}");
    }

    /// <summary>Kills the plugin with every process it started, and waits up to <see cref="KillWait"/> for it to be gone.</summary>
    private async Task KillAsync()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            // It ended meanwhile, or the system refused part of the tree; the wait below tells which.
        }

        try
        {
            await Exited.WaitAsync(KillWait, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn($"Plugin {_launch.PluginId} (pid {Id}) did not end within {Seconds(KillWait)} s of being killed");
        }
    }

    /// <summary>
    /// Lets go of the plugin: ends its group, which kills whatever of it is left, and releases the process handle once
    /// the process has ended. One that will not end keeps it, since its exit code is still to be read.
    /// </summary>
    private void Release()
    {
        _released = true;
        _group.Dispose();
        if (Exited.IsCompleted)
        {
            _process.Dispose();
            _ended.Dispose();
        }
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The stdout line a plugin announces itself with: <c>{"dictumPlugin":1,"url":"http://127.0.0.1:port"}</c>.</summary>
internal sealed record PluginReadyLine(int? DictumPlugin, string? Url);
