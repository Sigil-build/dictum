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
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host;
using Dictum.Host.Ipc;
using Dictum.Setup;

namespace Dictum.App.Shell;

/// <summary>
/// <c>dictum-gui --self-test</c>: checks what a Native AOT build could have dropped, prints one line per check and
/// returns 0 when all pass. CI runs it after every publish.
/// </summary>
public static class SelfTest
{
    /// <summary>The tray's SVG set: idle, recording and error in colour and in mono, paused, and the processing frames.</summary>
    private const int TrayIconFiles = 11;

    // The overlay model check: rms 0.01 is −40 dBFS, at 60 frames a second.
    private const int MeterFrames = 120;
    private const int SpeakingFrames = 30;
    private const double FramesPerSecond = 60;
    private const float SpeakingRms = 0.01f;
    private const double ExpectedLevel = 0.588;
    private const double LevelTolerance = 0.01;

    /// <summary>The alpha a pixel needs to count as drawn in the overlay check; the tray icons need full alpha.</summary>
    private const byte OverlayInkAlpha = 32;

    private const string BaseColourToken = "color.bg.base";
    private const int BytesPerPixel = 4;

    /// <summary>Alpha is the last byte of each pixel the bitmap copies out.</summary>
    private const int AlphaOffset = 3;

    /// <param name="avalonia">
    /// False skips the checks that need a process of their own: Avalonia's platform (tests already run one) and the UI
    /// culture (changing it would leak into tests running in parallel).
    /// </param>
    public static int Run(TextWriter output, bool avalonia = true)
    {
        var checks = new Checks(output);
        var ukrainian = CheckStrings(checks);
        CheckSvgIcons(checks);
        CheckOverlayModel(checks);
        if (avalonia)
        {
            Program.BuildAvaloniaApp().SetupWithoutStarting();
            CheckTokens(checks, Application.Current!);
            CheckTrayIcons(checks);
            CheckOverlay(checks);
            checks.Add("settings views", SettingsViews(out var views), views);
            UiCulture.Apply(UiLanguages.Ukrainian);
            checks.Add("ui culture", Strings.MenuQuit == ukrainian, $"\"{Strings.MenuQuit}\" with {CultureInfo.CurrentUICulture.Name}");
        }

        return checks.Failures == 0 ? 0 : 1;
    }

    /// <summary>Both resource sets are in. Returns the Ukrainian text the UI culture check expects.</summary>
    private static string CheckStrings(Checks checks)
    {
        var english = Strings.ResourceManager.GetString(nameof(Strings.MenuQuit), CultureInfo.InvariantCulture) ?? "";
        var ukrainian = Strings.ResourceManager.GetString(nameof(Strings.MenuQuit), CultureInfo.GetCultureInfo(UiLanguages.Ukrainian)) ?? "";
        checks.Add("strings", english == "Quit Dictum" && ukrainian.Any(c => c is >= 'А' and <= 'я'), $"en \"{english}\", uk \"{ukrainian}\"");
        return ukrainian;
    }

    private static void CheckSvgIcons(Checks checks)
    {
        var icons = Path.Combine(Path.GetTempPath(), $"dictum-self-test-{Environment.ProcessId}");
        try
        {
            TraySvgIcons.WriteAll(icons);
            var files = Directory.GetFiles(icons, "*.svg").Length;
            checks.Add("svg icons", files == TrayIconFiles, $"{files} files");
        }
        finally
        {
            Directory.Delete(icons, recursive: true);
        }
    }

    /// <summary>The overlay's maths: half a second at −40 dBFS, then silence.</summary>
    private static void CheckOverlayModel(Checks checks)
    {
        var meter = new LevelMeter();
        meter.Reset(TimeSpan.Zero);
        var speaking = 0.0;
        for (var frame = 1; frame <= MeterFrames; frame++)
        {
            meter.Update(frame <= SpeakingFrames ? SpeakingRms : 0f, [], TimeSpan.FromSeconds(frame / FramesPerSecond));
            if (frame == SpeakingFrames)
            {
                speaking = meter.Level;
            }
        }

        checks.Add("overlay model", Math.Abs(speaking - ExpectedLevel) < LevelTolerance && meter.Quiet,
            string.Create(CultureInfo.InvariantCulture, $"level {speaking:0.00} at -40 dBFS, then silence"));
    }

    private static void CheckTokens(Checks checks, Application app)
    {
        var dark = app.TryGetResource(BaseColourToken, ThemeVariant.Dark, out var darkBrush) ? darkBrush : null;
        var light = app.TryGetResource(BaseColourToken, ThemeVariant.Light, out var lightBrush) ? lightBrush : null;
        checks.Add("tokens", dark is ISolidColorBrush d && light is ISolidColorBrush l && d.Color != l.Color, $"{BaseColourToken} in both themes");
    }

    private static void CheckTrayIcons(Checks checks)
    {
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

        checks.Add("tray icons", empty.Count == 0, empty.Count == 0 ? $"{drawn} bitmaps" : $"empty: {string.Join(", ", empty)}");
    }

    private static void CheckOverlay(Checks checks)
    {
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
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)OverlayTokens.Width, (int)OverlayTokens.Height));
                bitmap.Render(canvas);
                scenes++;
                if (!HasInk(bitmap, OverlayInkAlpha))
                {
                    blank.Add(reduced ? $"{phase} (reduced)" : phase.ToString());
                }
            }
        }

        checks.Add("overlay", blank.Count == 0, blank.Count == 0 ? $"{scenes} scenes" : $"blank: {string.Join(", ", blank)}");
    }

    /// <summary>
    /// Builds each Settings page's view, and the wizard's, from compiled XAML, with the pages wired as the shell wires
    /// them; a trimmed type or binding throws here.
    /// </summary>
    private static bool SettingsViews(out string detail)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"dictum-self-test-settings-{Environment.ProcessId}");
        try
        {
            var paths = new AppPaths(folder, folder);
            var store = new ConfigStore(paths.ConfigFile, NullLog.Instance);
            store.Save(new DictumConfig());
            var session = new SettingsSession(store, UiTimers.Instance, NullLog.Instance);
            session.Load();
            var offline = new Offline();
            var secrets = new UnavailableSecretStore();
            using var runner = new ProcessorRunner(paths, secrets, NullLog.Instance);
            var sounds = DictumHost.CreateSoundPlayer(NullLog.Instance);
            using var ownedSounds = sounds as IDisposable;
            var services = new ShellServices(
                paths, Catalog.BuiltIn, offline, new StatusViewModel(paths.ConfigFile, AppPaths.GuiLogName, () => { }), new FrameHub(TimeProvider.System),
                UiTimers.Instance, null, null, sounds, () => secrets, () => runner, () => IModelDownloads.None,
                () => Task.FromResult(new HardwareInfo(null, 0)), () => long.MaxValue, new SpeedMeasurements(),
                new DesktopIntegration((_, _) => Task.FromResult<ITrayPresenter?>(null), NullLog.Instance),
                _ => Task.CompletedTask, TimeProvider.System, NullLog.Instance);
            using var settings = new SettingsViewModel(session, SettingsPages.Create(session, services, new WindowClipboard(() => null), () => { }));
            var general = settings.Pages.OfType<GeneralPage>().Single();
            using var setup = new SetupCoordinator(services, store, () => { }, () => { }, () => { });
            ViewModelBase[] models = [.. settings.Pages, general.Overlay, general.Pasting, setup.Create()];
            var locator = new ViewLocator();
            var built = models.Select(model => locator.Build(model)).Where(view => view is UserControl).ToList();
            detail = $"{built.Count} of {models.Length} views";
            return built.Count == models.Length;
        }
        catch (Exception ex)
        {
            // Whatever a trimmed build throws here is the failure this check looks for.
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

    /// <summary>Prints one line per check and counts the failures.</summary>
    private sealed class Checks(TextWriter output)
    {
        public int Failures { get; private set; }

        public void Add(string name, bool ok, string detail)
        {
            output.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: {detail}");
            Failures += ok ? 0 : 1;
        }
    }

    private sealed class Offline : IDaemonChannel
    {
        public DaemonLink Link { get; } = new(DaemonLinkState.Stopped);

        public Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromException<IpcResponse>(new DaemonNotRunningException("self-test"));
    }

    private static bool HasInk(Bitmap bitmap, byte minimum = byte.MaxValue)
    {
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var pixels = new byte[width * height * BytesPerPixel];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * BytesPerPixel);
        }
        finally
        {
            handle.Free();
        }

        for (var i = AlphaOffset; i < pixels.Length; i += BytesPerPixel)
        {
            if (pixels[i] >= minimum)
            {
                return true;
            }
        }

        return false;
    }
}
