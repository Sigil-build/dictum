using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Dictum.Audio;
using Dictum.Cli.Output;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Host.Daemon;

namespace Dictum.Cli.Commands;

internal static class RecordCommand
{
    private const string ToStdout = "stdout";
    private const string ToClipboard = "clipboard";
    private const string ToPaste = "paste";

    /// <summary>The microphone delivers 50 frames per second, 20 ms each.</summary>
    private const int FramesPerSecond = 50;

    /// <summary>Frame times in the dump are rounded to the millisecond.</summary>
    private const int FrameTimeDecimals = 3;

    private const int MeterWidth = 24;

    /// <summary>The bar with its time and brackets, plus a margin.</summary>
    private const int MeterLineWidth = MeterWidth + 12;

    /// <summary>The meter shows the top 60 dB below full scale.</summary>
    private const float MeterRangeDb = 60f;

    /// <summary>About ten redraws per second.</summary>
    private const int MeterRedrawMs = 100;

    private static readonly TimeSpan FrameLength = TimeSpan.FromSeconds(1) / FramesPerSecond;
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(50);

    public static Command Create()
    {
        var options = new TranscriptionOptions();
        var duration = Durations.Option("--duration", "Stop after this long, e.g. 30s");
        var untilSilence = Durations.Option("--until-silence", "Stop after this much silence following speech, e.g. 2s");
        var device = new Option<string?>("--device", "-d") { Description = "Microphone name or id from 'dictum devices'" };
        var output = new Option<string>("--out")
        {
            Description = "Where the text goes: stdout, clipboard, or paste into the focused app",
            DefaultValueFactory = _ => ToStdout,
        };
        output.AcceptOnlyFromAmong(ToStdout, ToClipboard, ToPaste);
        var dumpFrames = new Option<string?>("--dump-frames") { Description = "Write level and spectrum frames to a JSON file (for the overlay prototype)" };
        var saveAudio = new Option<string?>("--save-audio") { Description = "Also save the recording as a 16 kHz WAV file, e.g. to check the microphone" };

        var command = new Command("record", "Record from the microphone, then transcribe. Enter or Ctrl+C stops, Esc cancels.")
        {
            duration,
            untilSilence,
            device,
            output,
            dumpFrames,
            saveAudio,
        };
        options.AddTo(command);

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var host = context.Host();
            var profile = host.ResolveProfile(options.Read(parse));
            var pipeline = host.CreatePipeline();
            if (parse.GetValue(saveAudio) is { } savePath)
            {
                pipeline.ClipRecorded += clip =>
                {
                    using (var file = File.Create(savePath))
                    {
                        Wav.WritePcm16(file, clip.Samples.Span, clip.SampleRate);
                    }

                    var peak = 0f;
                    foreach (var s in clip.Samples.Span)
                    {
                        peak = Math.Max(peak, Math.Abs(s));
                    }

                    context.Error.WriteLine($"Saved {clip.Duration.TotalSeconds:F1} s to {savePath} (level {Levels.ToDb(Levels.Rms(clip.Samples.Span)):F0} dBFS RMS, peak {Levels.ToDb(peak):F0} dBFS)");
                };
            }

            var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var frames = parse.GetValue(dumpFrames) is null ? null : new List<FrameJson>();
            var meter = new LevelMeter(context.Error, enabled: !Console.IsErrorRedirected);
            var silence = parse.GetValue(untilSilence) is { } quiet ? new SilenceDetector(quiet) : null;

            host.Audio.FrameCaptured += frame =>
            {
                frames?.Add(new FrameJson(Math.Round(frame.At.TotalSeconds, FrameTimeDecimals), frame.Rms, frame.Bands.ToArray()));
                meter.Update(frame);
                if (silence?.Push(frame.Rms, FrameLength) == true)
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
            var target = parse.GetValue(output);
            var sink = target == ToStdout ? null : InjectorOutput.For(
                context.Platform.Injector,
                target == ToPaste ? OutputMode.Paste : OutputMode.Clipboard,
                context.TryLoadConfig()?.Profiles.FirstOrDefault(p => p.Id == profile.Id)?.PasteShortcut ?? PasteShortcut.Auto,
                context.TryLoadConfig()?.General ?? new GeneralSettings());
            var result = await pipeline.StopAsync(profile, sink, token).ConfigureAwait(false);

            if (parse.GetValue(dumpFrames) is { } dumpPath && frames is not null)
            {
                var dump = new FrameDumpJson(FramesPerSecond, frames.FirstOrDefault()?.Bands.Length ?? 0, frames);
                await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(dump, CliJsonContext.Readable.FrameDumpJson), token).ConfigureAwait(false);
                context.Error.WriteLine($"Wrote {frames.Count} frames to {dumpPath}");
            }

            if (sink is not null && !parse.GetValue(options.Json))
            {
                context.Error.WriteLine(result.Text.Length == 0 ? "No speech detected." : target == ToPaste ? "Pasted." : "Copied to the clipboard.");
                return ExitCodes.Success;
            }

            return Results.Print(context, result, parse.GetValue(options.Json));
        }, ct));

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

            await Task.Delay(KeyPollInterval, CancellationToken.None).ConfigureAwait(false);
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
                if (_sinceDraw.ElapsedMilliseconds < MeterRedrawMs)
                {
                    return;
                }

                var level = Math.Clamp((Levels.ToDb(_peak) + MeterRangeDb) / MeterRangeDb, 0f, 1f);
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
                    writer.Write($"\r{new string(' ', MeterLineWidth)}\r");
                }
            }
        }
    }
}
