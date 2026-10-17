using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tray;
using Dictum.App.ViewModels;
using Dictum.App.ViewModels.Settings;
using Dictum.Audio.PortAudio;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Setup;

namespace Dictum.App.Shell;

/// <summary>
/// <c>dictum-gui --self-test</c>: checks what a Native AOT build could have dropped, prints one line per check and
/// returns 0 when all pass. CI runs it after every publish.
/// </summary>
public static class SelfTest
{
    /// <param name="avalonia">
    /// False skips the checks that need a process of their own: Avalonia's platform (tests already run one) and the UI
    /// culture (changing it would leak into tests running in parallel).
    /// </param>
    public static int Run(TextWriter output, bool avalonia = true)
    {
        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            output.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: {detail}");
            failures += ok ? 0 : 1;
        }

        var english = Strings.ResourceManager.GetString(nameof(Strings.MenuQuit), CultureInfo.InvariantCulture) ?? "";
        var ukrainian = Strings.ResourceManager.GetString(nameof(Strings.MenuQuit), CultureInfo.GetCultureInfo("uk")) ?? "";
        Check("strings", english == "Quit Dictum" && ukrainian.Any(c => c is >= 'А' and <= 'я'), $"en \"{english}\", uk \"{ukrainian}\"");

        var icons = Path.Combine(Path.GetTempPath(), $"dictum-self-test-{Environment.ProcessId}");
        try
        {
            TraySvgIcons.WriteAll(icons);
            var files = Directory.GetFiles(icons, "*.svg").Length;
            Check("svg icons", files == 11, $"{files} files");
        }
        finally
        {
            Directory.Delete(icons, recursive: true);
        }

        // The overlay's maths: half a second at −40 dBFS, then silence.
        var meter = new LevelMeter();
        meter.Reset(TimeSpan.Zero);
        var speaking = 0.0;
        for (var frame = 1; frame <= 120; frame++)
        {
            meter.Update(frame <= 30 ? 0.01f : 0f, [], TimeSpan.FromSeconds(frame / 60.0));
            if (frame == 30)
            {
                speaking = meter.Level;
            }
        }

        Check("overlay model", Math.Abs(speaking - 0.588) < 0.01 && meter.Quiet,
            string.Create(CultureInfo.InvariantCulture, $"level {speaking:0.00} at -40 dBFS, then silence"));

        if (avalonia)
        {
            Program.BuildAvaloniaApp().SetupWithoutStarting();
            var app = Application.Current!;
            var dark = app.TryGetResource("color.bg.base", ThemeVariant.Dark, out var darkBrush) ? darkBrush : null;
            var light = app.TryGetResource("color.bg.base", ThemeVariant.Light, out var lightBrush) ? lightBrush : null;
            Check("tokens", dark is ISolidColorBrush d && light is ISolidColorBrush l && d.Color != l.Color, "color.bg.base in both themes");

            var drawn = 0;
            var empty = new List<string>();
            foreach (var glyph in Enum.GetValues<TrayGlyph>())
            {
                foreach (var size in TrayGeometry.Sizes)
                {
                    using var bitmap = TrayIconRenderer.Render(glyph, 0, size, Color.Parse(TrayGeometry.MonoOnDark), colourLayer: true);
                    _ = new WindowIcon(bitmap);
                    drawn++;
                    if (!HasInk(bitmap))
                    {
                        empty.Add($"{glyph}@{size}");
                    }
                }
            }

            Check("tray icons", empty.Count == 0, empty.Count == 0 ? $"{drawn} bitmaps" : $"empty: {string.Join(", ", empty)}");

            var canvas = new OverlayCanvas();
            var blank = new List<string>();
            var scenes = 0;
            foreach (var reduced in new[] { false, true })
            {
                foreach (var phase in Enum.GetValues<OverlayPhase>().Where(p => p != OverlayPhase.Hidden))
                {
                    canvas.Scene = new OverlayScene(phase, 0.15, 1, 1, 0.6, false, [0.5, 0.4, 0.3, 0.2, 0.1], "UK/EN", "Microphone not found", reduced, OverlayPalette.Aurora);
                    canvas.Measure(Size.Infinity);
                    canvas.Arrange(new Rect(canvas.DesiredSize));
                    using var bitmap = new RenderTargetBitmap(new PixelSize(480, 96));
                    bitmap.Render(canvas);
                    scenes++;
                    if (!HasInk(bitmap, 32))
                    {
                        blank.Add(reduced ? $"{phase} (reduced)" : phase.ToString());
                    }
                }
            }

            Check("overlay", blank.Count == 0, blank.Count == 0 ? $"{scenes} scenes" : $"blank: {string.Join(", ", blank)}");

            Check("settings views", SettingsViews(out var views), views);

            UiCulture.Apply("uk");
            Check("ui culture", Strings.MenuQuit == ukrainian, $"\"{Strings.MenuQuit}\" with {CultureInfo.CurrentUICulture.Name}");
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Builds each Settings page's view from compiled XAML; a trimmed type or binding throws here.</summary>
    private static bool SettingsViews(out string detail)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"dictum-self-test-settings-{Environment.ProcessId}");
        try
        {
            var store = new ConfigStore(Path.Combine(folder, "config.json"), NullLog.Instance);
            store.Save(new DictumConfig());
            var session = new SettingsSession(store, UiTimers.Instance, NullLog.Instance);
            session.Load();
            var offline = new Offline();
            using var runner = new ProcessorRunner(new AppPaths(folder, folder), new UnavailableSecretStore(), NullLog.Instance);
            var general = new GeneralPage(session, new GeneralServices(offline, new FrameHub(TimeProvider.System), UiTimers.Instance,
                null, null, new SoundPlayer(NullLog.Instance), TimeProvider.System, NullLog.Instance));
            SettingsPage[] pages =
            [
                general,
                general.Overlay,
                general.Pasting,
                new ProfilesPage(session, offline, NullLog.Instance),
                new SpeechProvidersPage(session, new SpeechServices(offline, new FrameHub(TimeProvider.System), UiTimers.Instance,
                    new UnavailableSecretStore(), _ => [], _ => false, _ => Task.CompletedTask, TimeProvider.System, NullLog.Instance)),
                new ProcessorsPage(session, new ProcessorsServices(runner, new UnavailableSecretStore(), new PromptFiles(Path.Combine(folder, "prompts")),
                    UiTimers.Instance, NullLog.Instance)),
                new ModelsPage(session, new ModelsServices(new AppPaths(folder, folder), Catalog.BuiltIn, ModelDownloads.None,
                    () => Task.FromResult(new HardwareInfo(null, 0)), () => long.MaxValue, 0, 1, new SpeedMeasurements(), offline, UiTimers.Instance,
                    _ => Task.CompletedTask, TimeProvider.System, NullLog.Instance)),
                new SystemPage(new SystemServices(offline, new StatusViewModel("config.json", "dictum-gui.log", () => { }),
                    () => OverlayAvailability.Row(OverlayHost.Windows, true), () => TrayVisibility.Row(TrayDesktop.Other, true, null, null),
                    UiTimers.Instance, TimeProvider.System, "self-test")),
                new HistoryPage(session, Path.Combine(folder, "history.jsonl"), new WindowClipboard(() => null), runner, NullLog.Instance),
            ];
            var locator = new ViewLocator();
            var built = pages.Select(page => locator.Build(page)).Where(view => view is UserControl).ToList();
            detail = $"{built.Count} of {pages.Length} views";
            return built.Count == pages.Length;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private sealed class Offline : IDaemonChannel
    {
        public DaemonLink Link { get; } = new(DaemonLinkState.Stopped);

        public Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromException<IpcResponse>(new DaemonNotRunningException("self-test"));
    }

    private static bool HasInk(Bitmap bitmap, byte minimum = 255)
    {
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] >= minimum)
            {
                return true;
            }
        }

        return false;
    }
}
