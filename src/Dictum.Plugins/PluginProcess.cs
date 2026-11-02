using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dictum.Core.Diagnostics;

namespace Dictum.Plugins;

/// <summary>
/// One running plugin program under protocol 1. It gets a fresh key in its environment, announces its address in a
/// ready line on stdout, and is ready once <c>/health</c> answers 200 and <c>/dictum/v1/info</c> names protocol 1.
/// Both of its output streams are read for as long as it runs, so a talkative plugin never blocks on a full pipe.
/// Its stdin is a pipe that only Dictum holds: <see cref="StopAsync"/> closes it, the plugin's sign to exit, and kills
/// the process tree after the grace. If Dictum itself dies, the pipe closes with it.
/// </summary>
public sealed class PluginProcess : IAsyncDisposable
{
    private const int KeyBytes = 32;
    private const int KeptLines = 20;
    private const string HealthPath = "/health";
    private const string InfoPath = "/dictum/v1/info";
    private const string Hint = "See its output above, and the plugin's own documentation.";

    /// <summary>
    /// How long a killed plugin has to be gone, its output drained included. Apart from the grace, which is the
    /// plugin's time to exit by itself and may be short.
    /// </summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly PluginLaunch _launch;
    private readonly ILog _log;
    private readonly TimeSpan _stopGrace;
    private readonly Process _process;
    private readonly Lock _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly TaskCompletionSource<PluginReadyLine> _readyLine = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cancelled once the process has ended, so that every wait of the start ends with it.</summary>
    private readonly CancellationTokenSource _ended = new();

    /// <summary>One stop, however many callers ask for it.</summary>
    private readonly Lazy<Task> _stopping;

    /// <exception cref="PluginUnavailableException">The system refused to start the program.</exception>
    private PluginProcess(PluginLaunch launch, string key, ILog log, TimeSpan stopGrace)
    {
        _launch = launch;
        _log = log;
        _stopGrace = stopGrace;
        _stopping = new Lazy<Task>(StopCoreAsync);
        _process = new Process { StartInfo = StartInfo(launch, key) };
        _process.OutputDataReceived += (_, e) => OnLine(e.Data, fromStdout: true);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data, fromStdout: false);
        try
        {
            _process.Start();
        }
        catch (Win32Exception ex)
        {
            _process.Dispose();
            throw new PluginUnavailableException($"Plugin {launch.PluginId} could not be started: {ex.Message}", Hint);
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Id = _process.Id;
        Exited = WatchExitAsync();
    }

    public int Id { get; }

    /// <summary>Set before <see cref="StartAsync"/> returns.</summary>
    public PluginEndpoint Endpoint { get; private set; } = null!;

    /// <summary>Completes with the exit code once the process has ended and the last of its output has been read.</summary>
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
    /// <exception cref="PluginUnavailableException">It could not start, exited, timed out or broke protocol 1.</exception>
    public static async Task<PluginProcess> StartAsync(PluginLaunch launch, HttpClient http, ILog log, PluginTimings timings, CancellationToken ct)
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(KeyBytes));
        var plugin = new PluginProcess(launch, key, log, timings.StopGrace);
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
                    throw plugin.Failure($"Plugin {launch.PluginId} did not get ready within {Seconds(timings.StartTimeout)} s.");
                }

                var code = await plugin.Exited.ConfigureAwait(false);
                throw plugin.Failure($"Plugin {launch.PluginId} exited with code {code} while starting.");
            }
        }
        catch
        {
            await plugin.KillAsync().ConfigureAwait(false);
            plugin.Release();
            throw;
        }

        log.Info($"Plugin {launch.PluginId} is ready at {plugin.Endpoint.Url} (pid {plugin.Id})");
        return plugin;
    }

    /// <summary>Closes the plugin's stdin, and kills its process tree if it has not exited within the grace.</summary>
    public Task StopAsync() => _stopping.Value;

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static ProcessStartInfo StartInfo(PluginLaunch launch, string key)
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
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add("0");
        if (launch.Model is { } model)
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(model);
        }

        if (launch.Threads is { } threads)
        {
            info.ArgumentList.Add("--threads");
            info.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
        }

        info.Environment[PluginProtocol.KeyVariable] = key;
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
            throw Failure($"Plugin {_launch.PluginId} announced {ready.Url}, which is not on this computer.");
        }

        await WaitUntilHealthyAsync(http, url, key, healthPoll, ct).ConfigureAwait(false);
        var info = await GetInfoAsync(http, url, key, ct).ConfigureAwait(false);
        if (info.Protocol != PluginProtocol.Version)
        {
            throw WrongProtocol(info.Protocol);
        }

        return new PluginEndpoint(url, key, info);
    }

    private static bool IsOnThisComputer(Uri url) =>
        url.Scheme == Uri.UriSchemeHttp && url.DnsSafeHost is "127.0.0.1" or "::1" or "localhost";

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
            }
            catch (HttpRequestException)
            {
                // Not listening yet: a refused connection waits, like a 503 while the model loads.
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
            throw Failure($"Plugin {_launch.PluginId} did not answer {InfoPath}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw Failure($"Plugin {_launch.PluginId} answered {InfoPath} with invalid JSON: {ex.Message}");
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

    /// <summary>Every line of either stream goes to the log and the recent output, except the ready line it waits for.</summary>
    private void OnLine(string? line, bool fromStdout)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        if (fromStdout && !_readyLine.Task.IsCompleted && ParseReadyLine(line) is { } ready)
        {
            _readyLine.TrySetResult(ready);
            return;
        }

        _log.Info($"plugin {_launch.PluginId}: {line}");
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
        // With both streams read asynchronously, this also waits until their last lines have been handled.
        await _process.WaitForExitAsync().ConfigureAwait(false);
        var code = _process.ExitCode;
        _log.Info($"Plugin {_launch.PluginId} (pid {Id}) exited with code {code}");
        await _ended.CancelAsync().ConfigureAwait(false);
        return code;
    }

    private async Task StopCoreAsync()
    {
        try
        {
            _process.StandardInput.Dispose();
        }
        catch (IOException)
        {
            // It has gone already, and its end of the pipe with it.
        }

        try
        {
            await Exited.WaitAsync(_stopGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn($"Plugin {_launch.PluginId} (pid {Id}) did not exit within {Seconds(_stopGrace)} s of its input closing; ending it");
            await KillAsync().ConfigureAwait(false);
        }

        Release();
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
            await Exited.WaitAsync(KillWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn($"Plugin {_launch.PluginId} (pid {Id}) did not end within {Seconds(KillWait)} s of being killed");
        }
    }

    /// <summary>
    /// Releases the process handle once the process has ended. One that will not end keeps it, since its exit code is
    /// still to be read.
    /// </summary>
    private void Release()
    {
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
