using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Dictum.TestPlugin;

/// <summary>
/// A plugin under protocol 1 whose run takes the numbered steps below; <see cref="TestPluginBehaviour"/> changes each of
/// them. They are numbered in the order a run takes them, which is not quite the order of the code: the endpoints that
/// answer steps 8-11 are set up before the listener starts, so they come ahead of the ready line, steps 6-7. It reads the
/// behaviour from <c>--model</c> when that is a <c>.json</c> file, else from <c>behaviour.json</c> next to the program,
/// else uses the defaults. Started with <c>--child</c>, it is instead the process that
/// <see cref="TestPluginBehaviour.LeaveChild"/> leaves running.
/// </summary>
internal static class Program
{
    private const string KeyVariable = "DICTUM_PLUGIN_KEY";
    private const string BehaviourFile = "behaviour.json";
    private const string ChildOption = "--child";
    private const string FollowStdinOption = "--follow-stdin";
    private const string SayAfterOption = "--say-after";

    /// <summary>The exit code of the crash that <see cref="TestPluginBehaviour.CrashAfterInfoMs"/> plays.</summary>
    private const int CrashCode = 1;

    /// <summary>What ASP.NET Core sends for JSON, as the test plugin did while it ran on it.</summary>
    private const string JsonType = "application/json; charset=utf-8";

    /// <summary>How long a child that <see cref="TestPluginBehaviour.LeaveChild"/> starts runs, unless it is ended first.</summary>
    private static readonly TimeSpan ChildLifetime = TimeSpan.FromMinutes(1);

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains(ChildOption))
        {
            var sayAfter = Option(args, SayAfterOption) is { } ms ? int.Parse(ms, CultureInfo.InvariantCulture) : (int?)null;
            return await RunAsChildAsync(args.Contains(FollowStdinOption), sayAfter);
        }

        var started = Stopwatch.StartNew();

        // 1. Only Dictum starts a plugin, and it always gives the key.
        var key = Environment.GetEnvironmentVariable(KeyVariable);
        if (string.IsNullOrEmpty(key))
        {
            await Console.Error.WriteLineAsync($"{KeyVariable} is not set.");
            return 2;
        }

        var model = Option(args, "--model");
        var threads = Option(args, "--threads") is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : (int?)null;
        var behaviour = Behaviour(model);

        // 2-4.
        if (behaviour.AppendAtStart is { } file)
        {
            await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, file), "ran\n");
        }

        foreach (var line in behaviour.Stderr)
        {
            await Console.Error.WriteLineAsync(line);
        }

        if (behaviour.ExitAtStart is { } code)
        {
            return code;
        }

        if (behaviour.Banner is { } banner)
        {
            Console.WriteLine(banner);
        }

        // The end of the plugin: 0 when its stdin closes, or a crash.
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var infoSent = 0;

        // 5. A small server of its own (TestHttpServer): it prints nothing, so stdout holds only what the steps print.
        // Every request needs the key; 8-11 answer the rest.
        using var server = new TestHttpServer(request =>
            request.Header("Authorization") != $"Bearer {key}" ? TestHttpResponse.Empty(401)
            : (request.Method, request.Path) switch
            {
                ("GET", "/health") => Health(behaviour, started),
                ("GET", "/dictum/v1/info") => Info(behaviour, model, args, threads, ref infoSent, exit),
                ("POST", "/v1/audio/transcriptions") => Transcribe(request, behaviour),
                _ => TestHttpResponse.Empty(404),
            });
        server.Start();

        // 6-7.
        if (!behaviour.NoReadyLine)
        {
            Console.WriteLine(JsonSerializer.Serialize(new ReadyLine(1, behaviour.ReadyUrl ?? server.Url + behaviour.ReadyPath), AnswerJson.Default.ReadyLine));
        }

        Flood(Console.Out, "out", behaviour.FloodLines);
        Flood(Console.Error, "err", behaviour.FloodLines);
        if (behaviour.AfterReady is { } afterReady)
        {
            Console.WriteLine(afterReady);
        }

        // 12.
        if (!behaviour.IgnoreStdin)
        {
            _ = Task.Run(() =>
            {
                ReadToEnd(Console.OpenStandardInput());
                exit.TrySetResult(0);
            });
        }

        // A request still under way is cut off: Dictum has stopped asking.
        return await exit.Task;
    }

    /// <summary>8-10.</summary>
    private static TestHttpResponse Health(TestPluginBehaviour behaviour, Stopwatch started)
    {
        if (behaviour.HealthCutShort)
        {
            // Promises 100 bytes, sends one, and drops the connection.
            return TestHttpResponse.Text(200, "{", JsonType) with { PromisedLength = 100 };
        }

        return behaviour.HealthStatus is { } status ? Error(status)
            : started.ElapsedMilliseconds < behaviour.LoadMs ? Json(503, new Health("loading"), AnswerJson.Default.Health)
            : Json(200, new Health("ready"), AnswerJson.Default.Health);
    }

    /// <summary>11. Dictum counts the plugin ready once it has this answer, so a crash is timed from the first one sent.</summary>
    private static TestHttpResponse Info(
        TestPluginBehaviour behaviour, string? model, string[] args, int? threads, ref int infoSent, TaskCompletionSource<int> exit)
    {
        Action? sent = null;
        if (behaviour.CrashAfterInfoMs is { } crashAfter && Interlocked.Exchange(ref infoSent, 1) == 0)
        {
            sent = () => _ = Task.Delay(crashAfter).ContinueWith(
                _ =>
                {
                    if (behaviour.LeaveChild)
                    {
                        StartChild(behaviour);
                    }

                    exit.TrySetResult(CrashCode);
                },
                TaskScheduler.Default);
        }

        var answer = behaviour.InfoStatus is { } status ? Error(status)
            : behaviour.InfoBody is { } body ? TestHttpResponse.Text(200, body, "application/json")
            : Json(
                200,
                new Info(
                    behaviour.InfoProtocol,
                    behaviour.Name,
                    behaviour.Version,
                    model is null ? null : new ModelInfo(ModelId(model), behaviour.Languages, behaviour.SetLanguage, Prompt: true),
                    args,
                    threads,
                    Environment.CurrentDirectory,
                    Variables()),
                AnswerJson.Default.Info);
        return answer with { Sent = sent };
    }

    private static TestHttpResponse Transcribe(TestHttpRequest request, TestPluginBehaviour behaviour)
    {
        if (behaviour.ExitOnTranscribe)
        {
            // No shutdown and no answer: the connection drops as the process goes.
            using var self = Process.GetCurrentProcess();
            self.Kill();
        }

        if (behaviour.TranscribeBody is { } body)
        {
            return TestHttpResponse.Text(behaviour.TranscribeStatus ?? 200, body, "application/json");
        }

        if (behaviour.TranscribeStatus is { } status)
        {
            return Error(status);
        }

        var text = behaviour.Text;
        if (behaviour.Echo)
        {
            var form = TestForm.Read(request.Header("Content-Type"), request.Body);
            text = string.Create(
                CultureInfo.InvariantCulture,
                $"language={form["language"]};languages={form["languages"]};prompt={form["prompt"]};model={form["model"]};response_format={form["response_format"]};file={form.FileName};type={form.FileContentType};bytes={form.FileLength}");
        }

        return Json(200, new Transcription(text, behaviour.Language), AnswerJson.Default.Transcription);
    }

    private static TestHttpResponse Error(int status)
    {
        var message = string.Create(CultureInfo.InvariantCulture, $"The test plugin answers {status}.");
        return Json(status, new ErrorAnswer(new ErrorDetail(message)), AnswerJson.Default.ErrorAnswer);
    }

    private static TestHttpResponse Json<T>(int status, T value, JsonTypeInfo<T> type) =>
        new(status, JsonSerializer.SerializeToUtf8Bytes(value, type), JsonType);

    /// <summary>
    /// A copy of this program, with no stream redirected: it inherits this process's stdin, stdout and stderr, so it holds
    /// the pipes Dictum reads. Its pid goes to <see cref="TestPluginBehaviour.ChildPidFile"/>, written whole.
    /// </summary>
    private static void StartChild(TestPluginBehaviour behaviour)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add(ChildOption);
        if (behaviour.ChildFollowsStdin)
        {
            start.ArgumentList.Add(FollowStdinOption);
        }

        if (behaviour.ChildSaysAfterMs is { } sayAfter)
        {
            start.ArgumentList.Add(SayAfterOption);
            start.ArgumentList.Add(sayAfter.ToString(CultureInfo.InvariantCulture));
        }

        using var child = Process.Start(start)!;
        var pidFile = Path.Combine(AppContext.BaseDirectory, TestPluginBehaviour.ChildPidFile);
        File.WriteAllText(pidFile + ".tmp", child.Id.ToString(CultureInfo.InvariantCulture));
        File.Move(pidFile + ".tmp", pidFile, overwrite: true);
    }

    /// <summary>The process <see cref="StartChild"/> starts: it runs for <see cref="ChildLifetime"/> at most, and prints a line if asked.</summary>
    private static async Task<int> RunAsChildAsync(bool followStdin, int? sayAfterMs)
    {
        var lifetime = Task.Delay(ChildLifetime);
        if (sayAfterMs is { } ms)
        {
            _ = Task.Delay(ms).ContinueWith(_ => Console.WriteLine("child here"), TaskScheduler.Default);
        }

        if (followStdin)
        {
            await Task.WhenAny(lifetime, Task.Run(() => ReadToEnd(Console.OpenStandardInput())));
        }
        else
        {
            await lifetime;
        }

        return 0;
    }

    /// <summary>Every variable of its environment, which the tests check for what Dictum sets.</summary>
    private static Dictionary<string, string> Variables() =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(variable => (string)variable.Key, variable => (string?)variable.Value ?? "", StringComparer.Ordinal);

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static TestPluginBehaviour Behaviour(string? model)
    {
        if (model is not null && model.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(model))
        {
            return TestPluginBehaviour.Load(model);
        }

        var nextToProgram = Path.Combine(AppContext.BaseDirectory, BehaviourFile);
        return File.Exists(nextToProgram) ? TestPluginBehaviour.Load(nextToProgram) : new TestPluginBehaviour();
    }

    /// <summary>A model file's name without its extension, or a model folder's name.</summary>
    private static string ModelId(string model) => Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(model));

    /// <summary>One write of every line, which blocks until the reader has taken all but a pipe's buffer of it.</summary>
    private static void Flood(TextWriter writer, string stream, int lines)
    {
        if (lines <= 0)
        {
            return;
        }

        var text = new StringBuilder();
        for (var i = 1; i <= lines; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{stream} {i}").Append('\n');
        }

        writer.Write(text.ToString());
        writer.Flush();
    }

    private static void ReadToEnd(Stream stream)
    {
        using (stream)
        {
            var buffer = new byte[256];
            while (stream.Read(buffer) > 0)
            {
                // Dictum sends nothing; only the end matters.
            }
        }
    }
}

internal sealed record ReadyLine(int DictumPlugin, string Url);

internal sealed record Health(string Status);

/// <summary>
/// Protocol 1's info, plus what the tests check of the launch: the arguments, the threads, the working folder and the
/// environment.
/// </summary>
internal sealed record Info(
    int Protocol, string Name, string Version, ModelInfo? Model, IReadOnlyList<string> Argv, int? Threads, string Cwd, IReadOnlyDictionary<string, string> Env);

internal sealed record ModelInfo(string Id, IReadOnlyList<string> Languages, bool SetLanguage, bool Prompt);

internal sealed record Transcription(string Text, string? Language);

internal sealed record ErrorAnswer(ErrorDetail Error);

internal sealed record ErrorDetail(string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadyLine))]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(Info))]
[JsonSerializable(typeof(Transcription))]
[JsonSerializable(typeof(ErrorAnswer))]
internal sealed partial class AnswerJson : JsonSerializerContext;
