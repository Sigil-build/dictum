using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Stt;
using Dictum.Host;
using Dictum.Plugins;
using Dictum.Testing;

namespace Dictum.Integration.Tests.Plugins;

/// <summary>
/// The minimal plugin in <c>docs/guides/write-a-plugin.md</c>, run the way its reader would: the program, the launcher
/// for this system and the manifest are copied from the guide's <c>{#example}</c> section into a folder, and Dictum
/// starts it under protocol 1. It runs without DICTUM_INTEGRATION, so that CI's Linux job runs plugin.sh, python3
/// and the execute bit; skipped when the launcher's Python is not on PATH, or on a system the guide does not ship for.
/// </summary>
public sealed partial class WritePluginGuideTests : IDisposable
{
    private const string Guide = "docs/guides/write-a-plugin.md";
    private const string Section = "example";
    private const string Text = "hello from python";

    /// <summary>The systems Dictum is built for, which the guide's example must each have a program for.</summary>
    private static readonly string[] ShippedRids = ["win-x64", "linux-x64"];

    private readonly TempDirectory _dir = new();
    private readonly ListLog _log = new();
    private readonly HttpClient _http = PluginHttp.CreateClient();

    /// <summary>The interpreter the guide's launchers call: <c>plugin.cmd</c> runs python, <c>plugin.sh</c> python3.</summary>
    private static string Python => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>The fence of the launcher for this system: a batch file on Windows, a shell script elsewhere.</summary>
    private static string LauncherFence => OperatingSystem.IsWindows() ? "bat" : "sh";

    public void Dispose()
    {
        _http.Dispose();
        _dir.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task TheGuidesPythonExampleIsAWorkingPlugin()
    {
        Assert.SkipUnless(CanRun(Python), $"{Python} is not on PATH");
        var ct = TestContext.Current.CancellationToken;

        // A folder with a space and Cyrillic letters, as under C:\Users\Євген: the launcher must quote its own path.
        var folder = WriteExample(Path.Combine(_dir.Path, "плагін hello"));
        var manifest = PluginManifest.Load(folder);

        // A model folder with a space and Cyrillic letters too, as one under the user's own folder has: it is passed on whole.
        var model = Directory.CreateDirectory(Path.Combine(_dir.Path, "моделі", manifest.Id, "tiny модель")).FullName;

        // As 'dictum plugins add' runs it: no model, so it only says who it is.
        await using (var bare = await StartAsync(Launch(folder, manifest, model: null), ct))
        {
            Assert.Equal(1, bare.Endpoint.Info.Protocol);
            Assert.Null(bare.Endpoint.Info.Model);
            await StopsWithinTheGraceAsync(bare);
        }

        var plugin = await StartAsync(Launch(folder, manifest, model), ct);
        try
        {
            Assert.Equal("tiny модель", plugin.Endpoint.Info.Model?.Id);

            using var keyless = await _http.GetAsync(new Uri(plugin.Endpoint.Url, "/health"), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, keyless.StatusCode);

            // Every form PluginStt builds: one language, a list to pick from, and none.
            foreach (var request in new SttRequest[] { new("en", "Dictum, Whisper"), new(null, null, ["uk", "en"]), new(LanguageList.Auto, null), new(null, null) })
            {
                var (status, body) = await TranscribeAsync(plugin.Endpoint, request, ct);
                Assert.True(status == HttpStatusCode.OK, $"{request}: {(int)status}: {body}");
                using var answer = JsonDocument.Parse(body);
                Assert.Equal(Text, answer.RootElement.GetProperty("text").GetString());
            }

            // The same form without the key.
            using var unsigned = new HttpRequestMessage(HttpMethod.Post, new Uri(plugin.Endpoint.Url, "/v1/audio/transcriptions"))
            {
                Content = await FormAsync(new SttRequest("en", null)),
            };
            using var refused = await _http.SendAsync(unsigned, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        finally
        {
            await StopsWithinTheGraceAsync(plugin);
        }
    }

    /// <summary>
    /// The guide says to start the stdin watcher first, before a model loads, so that a plugin notices a Dictum that
    /// died even during a long load; its example does as it says, before the point where a model would load.
    /// </summary>
    [Fact]
    public void TheExampleWatchesStdinBeforeAnythingElseStarts()
    {
        var program = Fence(ExampleFences(), "python");
        var watcher = program.IndexOf("threading.Thread(target=exit_when_stdin_closes", StringComparison.Ordinal);
        var server = program.IndexOf("ThreadingHTTPServer((", StringComparison.Ordinal);

        Assert.True(watcher >= 0, $"{Guide}: the example starts no thread that watches stdin");
        Assert.True(watcher < program.IndexOf("def model_info", StringComparison.Ordinal) && watcher < server, $"{Guide}: the example starts its stdin watcher after the rest");
    }

    /// <summary>A polite plugin exits by itself, with code 0, once its stdin closes; one that does not is killed after the grace.</summary>
    private static async Task StopsWithinTheGraceAsync(PluginProcess plugin)
    {
        var clock = Stopwatch.StartNew();
        await plugin.StopAsync();

        Assert.True(clock.Elapsed < PluginTimings.Default.StopGrace, $"stopped after {clock.ElapsedMilliseconds} ms, so it was killed");
        Assert.Equal(0, await plugin.Exited);
    }

    /// <summary>Protocol 1's form for <paramref name="request"/>, as the plugin provider sends it, with the key.</summary>
    private async Task<(HttpStatusCode Status, string Body)> TranscribeAsync(PluginEndpoint endpoint, SttRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.Url, "/v1/audio/transcriptions")) { Content = await FormAsync(request) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Key);
        using var response = await _http.SendAsync(message, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The form <see cref="PluginStt"/> builds, checked for the field that tells the plugin the language.</summary>
    private static async Task<MultipartFormDataContent> FormAsync(SttRequest request)
    {
        var form = PluginStt.BuildForm("tiny", new AudioClip(new float[AudioClip.WhisperSampleRate / 2]), request);
        var text = await form.ReadAsStringAsync(TestContext.Current.CancellationToken);
        switch (request)
        {
            case { Language: { } language } when language != LanguageList.Auto:
                Assert.Contains($"name=language\r\n\r\n{language}\r\n", text, StringComparison.Ordinal);
                Assert.DoesNotContain("name=languages", text, StringComparison.Ordinal);
                break;
            case { Language: null, Candidates: { Count: > 0 } list }:
                Assert.Contains($"name=languages\r\n\r\n{string.Join(',', list)}\r\n", text, StringComparison.Ordinal);
                break;
            default:
                Assert.DoesNotContain("name=language", text, StringComparison.Ordinal);
                break;
        }

        return form;
    }

    private async Task<PluginProcess> StartAsync(PluginLaunch launch, CancellationToken ct)
    {
        try
        {
            return await PluginProcess.StartAsync(launch, _http, PlatformFactory.CreateProcessGroups(), _log, PluginTimings.Default, ct);
        }
        catch (PluginUnavailableException ex)
        {
            Assert.Fail($"{ex.Message}\nIts output:\n{string.Join('\n', ex.Output)}");
            throw;
        }
    }

    private static PluginLaunch Launch(string folder, PluginManifest manifest, string? model) =>
        new(manifest.Id, manifest.Name, manifest.Version, folder, manifest.ExecutablePath(folder), model, Threads: null);

    /// <summary>
    /// Writes the example's program, its manifest, and the launcher that the manifest names for this system, each from
    /// the first fence of its kind in the guide's example section.
    /// </summary>
    private static string WriteExample(string folder)
    {
        var fences = ExampleFences();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.py"), Fence(fences, "python"));

        var manifest = Fence(fences, "json");
        File.WriteAllText(Path.Combine(folder, PluginProtocol.ManifestFile), manifest);
        using var json = JsonDocument.Parse(manifest);
        var rid = PluginProtocol.CurrentRid;
        var listed = json.RootElement.GetProperty("executable").TryGetProperty(rid, out var program);

        // The example serves the systems Dictum ships for; another system it may leave out.
        Assert.SkipUnless(listed || ShippedRids.Contains(rid), $"The guide's manifest has no program for {rid}, which Dictum does not ship for");
        Assert.True(listed, $"{Guide}: the example's plugin.json has no program for {rid}");

        var launcher = Path.Combine(folder, program.GetString()!);
        var text = Fence(fences, LauncherFence);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(launcher, text.ReplaceLineEndings("\r\n"));
        }
        else
        {
            File.WriteAllText(launcher, text);
            // 755, as 'chmod +x' leaves a new file.
            File.SetUnixFileMode(
                launcher,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return folder;
    }

    private static string Fence(List<(string Info, string Text)> fences, string info)
    {
        var found = fences.FindIndex(fence => fence.Info == info);
        Assert.True(found >= 0, $"{Guide} has no ```{info} block under {{#{Section}}}");
        return fences[found].Text;
    }

    /// <summary>The fenced blocks of the guide's <c>{#example}</c> section, in order: each one's info string and its lines.</summary>
    private static List<(string Info, string Text)> ExampleFences()
    {
        var path = Path.Combine(Repository.Root, Guide);
        Assert.True(File.Exists(path), $"{Guide} does not exist");

        var fences = new List<(string Info, string Text)>();
        var inSection = false;
        (string Marker, string Info, int Indent, List<string> Lines)? open = null;
        foreach (var line in File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (open is { } fence)
            {
                if (trimmed.Length >= fence.Marker.Length && trimmed.Trim(fence.Marker[0]).Length == 0)
                {
                    if (inSection)
                    {
                        fences.Add((fence.Info, string.Concat(fence.Lines.Select(l => l + "\n"))));
                    }

                    open = null;
                }
                else
                {
                    fence.Lines.Add(line[Math.Min(fence.Indent, line.Length - line.TrimStart().Length)..]);
                }
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = trimmed.EndsWith($"{{#{Section}}}", StringComparison.Ordinal);
            }
            else if (FenceOpen().Match(trimmed) is { Success: true } start)
            {
                open = (start.Groups["marker"].Value, start.Groups["info"].Value.Trim(), line.Length - line.TrimStart().Length, []);
            }
        }

        return fences;
    }

    /// <summary>Whether <paramref name="program"/> starts and answers <c>--version</c>; the Store's stand-in for Python on Windows does not.</summary>
    private static bool CanRun(string program)
    {
        var start = new ProcessStartInfo(program, "--version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(?<marker>`{3,}|~{3,})(?<info>[^`]*)$")]
    private static partial Regex FenceOpen();
}
