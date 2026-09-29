using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Dictum.Audio;
using Dictum.Cli.Output;
using Dictum.Core.Audio;

namespace Dictum.Cli.Commands;

internal static class RecordCommand
{
    private const int MeterWidth = 24;

    public static Command Create()
    {
        var options = new TranscriptionOptions();
        var duration = Durations.Option("--duration", "Stop after this long, e.g. 30s");
        var untilSilence = Durations.Option("--until-silence", "Stop after this much silence following speech, e.g. 2s");
        var device = new Option<string?>("--device", "-d") { Description = "Microphone name or id from 'dictum devices'" };
        var output = new Option<string>("--out")
        {
            Description = "Where the text goes: stdout (clipboard and paste come with the daemon)",
            DefaultValueFactory = _ => "stdout",
        };
        output.AcceptOnlyFromAmong("stdout");
        var dumpFrames = new Option<string?>("--dump-frames") { Description = "Write level and spectrum frames to a JSON file (for the overlay prototype)" };

        var command = new Command("record", "Record from the microphone, then transcribe. Enter or Ctrl+C stops, Esc cancels.")
        {
            duration,
            untilSilence,
            device,
            output,
            dumpFrames,
        };
        options.AddTo(command);

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            var host = context.Host();
            var profile = host.ResolveProfile(options.Read(parse));
            var pipeline = host.CreatePipeline();

            var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames = parse.GetValue(dumpFrames) is null ? null : new List<FrameJson>();
            var meter = new LevelMeter(context.Error, enabled: !Console.IsErrorRedirected);
            var silence = parse.GetValue(untilSilence) is { } quiet ? new SilenceDetector(quiet) : null;
            var frameLength = TimeSpan.FromMilliseconds(20);

            host.Audio.FrameCaptured += frame =>
            {
                frames?.Add(new FrameJson(Math.Round(frame.At.TotalSeconds, 3), frame.Rms, frame.Bands.ToArray()));
                meter.Update(frame);
                if (silence?.Push(frame.Rms, frameLength) == true)
                {
                    stop.TrySetResult(true);
                }
            };

            await pipeline.StartRecordingAsync(parse.GetValue(device) ?? context.TryLoadConfig()?.General.AudioDevice, token).ConfigureAwait(false);
            context.Error.WriteLine(Console.IsInputRedirected
                ? "Recording… press Ctrl+C to stop."
                : "Recording… press Enter or Ctrl+C to stop, Esc to cancel.");

            using (ConsoleInterrupt.Claim(() => stop.TrySetResult(true)))
            {
                _ = Console.IsInputRedirected ? Task.CompletedTask : WatchKeysAsync(stop, token);
                if (parse.GetValue(duration) is { } limit)
                {
                    _ = Task.Delay(limit, token).ContinueWith(_ => stop.TrySetResult(true), TaskScheduler.Default);
                }

                using var registration = token.Register(() => stop.TrySetResult(false));
                var proceed = await stop.Task.ConfigureAwait(false);
                meter.Clear();
                if (!proceed)
                {
                    await pipeline.CancelAsync().ConfigureAwait(false);
                    context.Error.WriteLine("Cancelled.");
                    return token.IsCancellationRequested ? ExitCodes.Interrupted : ExitCodes.Success;
                }
            }

            context.Error.WriteLine("Transcribing…");
            var result = await pipeline.StopAsync(profile, output: null, token).ConfigureAwait(false);

            if (parse.GetValue(dumpFrames) is { } dumpPath && frames is not null)
            {
                var dump = new FrameDumpJson(50, frames.FirstOrDefault()?.Bands.Length ?? 0, frames);
                await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(dump, CliJsonContext.Readable.FrameDumpJson), token).ConfigureAwait(false);
                context.Error.WriteLine($"Wrote {frames.Count} frames to {dumpPath}");
            }

            return Results.Print(context, result, parse.GetValue(options.Json));
        }));

        return command;
    }

    /// <summary>Enter stops and transcribes, Esc cancels.</summary>
    private static async Task WatchKeysAsync(TaskCompletionSource<bool> stop, CancellationToken ct)
    {
        while (!stop.Task.IsCompleted && !ct.IsCancellationRequested)
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.Enter)
                {
                    stop.TrySetResult(true);
                }
                else if (key == ConsoleKey.Escape)
                {
                    stop.TrySetResult(false);
                }
            }

            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>A one-line level meter on stderr, redrawn about ten times per second.</summary>
    private sealed class LevelMeter(TextWriter writer, bool enabled)
    {
        private readonly Lock _gate = new();
        private readonly Stopwatch _sinceDraw = Stopwatch.StartNew();
        private float _peak;

        public void Update(AudioFrame frame)
        {
            if (!enabled)
            {
                return;
            }

            lock (_gate)
            {
                _peak = Math.Max(_peak, frame.Rms);
                if (_sinceDraw.ElapsedMilliseconds < 100)
                {
                    return;
                }

                // Map -60..0 dBFS onto the bar.
                var level = Math.Clamp((Levels.ToDb(_peak) + 60f) / 60f, 0f, 1f);
                var filled = (int)Math.Round(level * MeterWidth);
                writer.Write($"\r  {frame.At:mm\\:ss} [{new string('#', filled)}{new string(' ', MeterWidth - filled)}]");
                _peak = 0;
                _sinceDraw.Restart();
            }
        }

        public void Clear()
        {
            if (enabled)
            {
                lock (_gate)
                {
                    writer.Write($"\r{new string(' ', MeterWidth + 12)}\r");
                }
            }
        }
    }
}
