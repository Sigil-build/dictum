using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dictum.TestPlugin;

/// <summary>
/// A plugin under protocol 1 that takes, in order, the numbered steps below; <see cref="TestPluginBehaviour"/> changes
/// each of them. It reads the behaviour from <c>--model</c> when that is a <c>.json</c> file, else from
/// <c>behaviour.json</c> next to the program, else uses the defaults.
/// </summary>
internal static class Program
{
    private const string KeyVariable = "DICTUM_PLUGIN_KEY";
    private const string BehaviourFile = "behaviour.json";

    /// <summary>The exit code of an <see cref="TestPluginBehaviour.ExitAfterReadyMs"/> exit, which plays a crash.</summary>
    private const int CrashCode = 1;

    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(2);

    public static async Task<int> Main(string[] args)
    {
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

        // 5. Kestrel's own log would print to stdout ahead of the ready line.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != $"Bearer {key}")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        // 8-10.
        app.MapGet("/health", () => started.ElapsedMilliseconds < behaviour.LoadMs
            ? Results.Json(new Health("loading"), AnswerJson.Default.Health, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(new Health("ready"), AnswerJson.Default.Health));
        app.MapGet("/dictum/v1/info", () => Results.Json(
            new Info(
                behaviour.InfoProtocol,
                behaviour.Name,
                behaviour.Version,
                model is null ? null : new ModelInfo(ModelId(model), behaviour.Languages, behaviour.SetLanguage, Prompt: true),
                args,
                threads,
                Environment.CurrentDirectory),
            AnswerJson.Default.Info));
        app.MapPost("/v1/audio/transcriptions", (HttpRequest request) => TranscribeAsync(request, behaviour));
        await app.StartAsync();

        // 6-7.
        if (!behaviour.NoReadyLine)
        {
            Console.WriteLine(JsonSerializer.Serialize(new ReadyLine(1, behaviour.ReadyUrl ?? app.Urls.First()), AnswerJson.Default.ReadyLine));
        }

        Flood(Console.Out, "out", behaviour.FloodLines);
        Flood(Console.Error, "err", behaviour.FloodLines);
        if (behaviour.AfterReady is { } afterReady)
        {
            Console.WriteLine(afterReady);
        }

        // 11-12.
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (behaviour.ExitAfterReadyMs is { } exitAfter)
        {
            _ = Task.Delay(exitAfter).ContinueWith(_ => exit.TrySetResult(CrashCode), TaskScheduler.Default);
        }

        if (!behaviour.IgnoreStdin)
        {
            _ = Task.Run(() =>
            {
                ReadToEnd(Console.OpenStandardInput());
                exit.TrySetResult(0);
            });
        }

        var exitCode = await exit.Task;
        using var stopping = new CancellationTokenSource(StopWithin);
        await app.StopAsync(stopping.Token);
        return exitCode;
    }

    private static async Task<IResult> TranscribeAsync(HttpRequest request, TestPluginBehaviour behaviour)
    {
        if (behaviour.TranscribeStatus is { } status)
        {
            var message = string.Create(CultureInfo.InvariantCulture, $"The test plugin answers {status}.");
            return Results.Json(new ErrorAnswer(new ErrorDetail(message)), AnswerJson.Default.ErrorAnswer, statusCode: status);
        }

        var form = await request.ReadFormAsync();
        var bytes = form.Files.GetFile("file")?.Length ?? 0;
        var text = behaviour.Echo
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"language={form["language"]};languages={form["languages"]};prompt={form["prompt"]};model={form["model"]};bytes={bytes}")
            : behaviour.Text;
        return Results.Json(new Transcription(text, behaviour.Language), AnswerJson.Default.Transcription);
    }

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

/// <summary>Protocol 1's info, plus what the tests check of the launch: the arguments, the threads and the working folder.</summary>
internal sealed record Info(int Protocol, string Name, string Version, ModelInfo? Model, IReadOnlyList<string> Argv, int? Threads, string Cwd);

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
