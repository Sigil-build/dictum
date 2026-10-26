using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Controls;
using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.Tests.Setup;
using Dictum.App.Tests.Updates;
using Dictum.App.Updates;
using Dictum.App.ViewModels;
using Dictum.App.ViewModels.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.App.Views.Settings;
using Dictum.App.Views.Setup;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Processing;
using Dictum.Setup;

namespace Dictum.App.Tests.Docs;

/// <summary>
/// The screenshots in <c>docs/images/</c>: the Settings pages, the setup wizard's steps and the overlay, each in English
/// and Ukrainian, light and dark, shown on a page of both languages. <see cref="Generate"/> draws them from the app's
/// own views on fakes, on Windows, so the fonts are the ones Windows users see.
/// </summary>
public sealed class ScreenshotTests(AvaloniaFixture fx)
{
    /// <summary>The Settings shots: the page each shows, and how the page is set up first.</summary>
    internal static readonly IReadOnlyList<SettingsShot> SettingsShots =
    [
        new("settings-general", SettingsPageKey.General),
        new("settings-overlay", SettingsPageKey.General, page => ((GeneralPage)page).OpenOverlayCommand.Execute(null)),
        new("settings-pasting", SettingsPageKey.General, page => ((GeneralPage)page).OpenPastingCommand.Execute(null)),
        new("settings-profiles", SettingsPageKey.Profiles),
        new("settings-speech", SettingsPageKey.SpeechProviders),
        new("settings-processors", SettingsPageKey.Processors),
        new("settings-models", SettingsPageKey.Models),
        new("settings-history", SettingsPageKey.History, page => ((HistoryPage)page).Selected = ((HistoryPage)page).Items[0]),
        new("settings-system", SettingsPageKey.System),
        new("settings-about", SettingsPageKey.About),
    ];

    /// <summary>The shots, each the name of four files in <c>docs/images/</c>.</summary>
    public static readonly IReadOnlyList<string> Shots =
    [
        .. SettingsShots.Select(shot => shot.Name),
        "setup-languages", "setup-model", "setup-cleanup", "setup-words", "setup-login", "setup-downloads",
        "setup-downloading", "setup-ready",
        OverlayShot(OverlayPhase.Listening), OverlayShot(OverlayPhase.Transcribing),
    ];

    private const string Variable = "DICTUM_SCREENSHOTS";

    private static readonly (string Code, string Formats)[] Languages = [("en", "en-US"), ("uk", "uk-UA")];

    private static readonly ThemeVariant[] Themes = [ThemeVariant.Light, ThemeVariant.Dark];

    /// <summary>The computer the shots show: a graphics card with 12 GB, 32 GB of memory, 8 cores and 120 GB free.</summary>
    private static readonly HardwareInfo Hardware = new("NVIDIA GeForce RTX 3060", 120_000_000_000, 12_884_901_888);

    private const long MemoryBytes = 34_359_738_368;

    private const int CpuCores = 8;

    private const int DaemonPid = 4242;

    private const string Executable = @"C:\Program Files\Dictum\dictum-gui.exe";

    /// <summary>The system About Dictum names: a Windows 11 release, whatever the machine that draws the shots runs.</summary>
    private const string ShownSystem = "Windows 11 (10.0.26100), x64";

    private const string HotkeyBackend = "low-level keyboard hook (SharpHook)";

    private const string PasteBackend = "Win32 clipboard + SendInput";

    private static readonly AudioDevice Headset = new("3", "Headset Microphone", "Windows WASAPI", 1, 48000, true);

    private static readonly AudioDevice UsbMicrophone = new("5", "USB Microphone", "Windows WASAPI", 1, 48000, false);

    /// <summary>Step 7: how far the speech model is, and how fast it comes.</summary>
    private const long DownloadedBytes = 350_000_000;

    private const long DownloadRate = 18_000_000;

    /// <summary>Where the shots say Dictum's folders are, instead of the temp folder they are in.</summary>
    private const string ShownAppData = @"C:\Users\user\AppData";

    /// <summary>
    /// The desktop behind the overlay in <see cref="OverlayPreview"/>. A colour, not a brush: an Avalonia object made
    /// by a static initializer would belong to the test thread that touched the class first, not to the UI thread.
    /// </summary>
    private const string DesktopColour = "#1E1F22";

    /// <summary>How long a shot waits for layout and the theme's transitions, which run in real time.</summary>
    private const int SettleFrames = 30;

    private const int FrameMilliseconds = 16;

    /// <summary>The smallest PNG files, which is what a repository wants of images it keeps.</summary>
    private static readonly PngBitmapEncoderOptions Png = new() { CompressionLevel = CompressionLevel.SmallestSize };

    private static string Docs => Path.Combine(Repository.Root, "docs");

    private static string Images => Path.Combine(Docs, "images");

    /// <summary>English light, Ukrainian light, English dark, Ukrainian dark. The site swaps <c>x.png</c> for <c>x.uk.png</c> on Ukrainian pages.</summary>
    private static string[] Files(string shot) => [$"{shot}.png", $"{shot}.uk.png", $"{shot}-dark.png", $"{shot}-dark.uk.png"];

    private static string FileName(string shot, string language, ThemeVariant theme) =>
        $"{shot}{(theme == ThemeVariant.Dark ? "-dark" : "")}{(language == "en" ? "" : "." + language)}.png";

    private static string OverlayShot(OverlayPhase phase) => "overlay-" + phase.ToString().ToLowerInvariant();

    // A page links the English light and dark files; GitHub and the site each show the one for the reader's colour scheme.
    [Fact]
    public void EveryScreenshotExistsAndIsShown()
    {
        var problems = new List<string>();
        var english = Directory.EnumerateFiles(Docs, "*.md", SearchOption.AllDirectories)
            .Where(page => !page.EndsWith(".uk.md", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToDictionary(page => page, File.ReadAllText);
        foreach (var shot in Shots)
        {
            problems.AddRange(Files(shot).Where(file => !File.Exists(Path.Combine(Images, file))).Select(file => $"docs/images/{file} does not exist"));

            string[] links = [$"images/{shot}.png#gh-light-mode-only", $"images/{shot}-dark.png#gh-dark-mode-only"];
            var pages = english.Where(page => links.All(link => page.Value.Contains(link, StringComparison.Ordinal))).Select(page => page.Key).ToList();
            if (pages.Count == 0)
            {
                problems.Add($"no English page shows {shot}: none links both {links[0]} and {links[1]}");
            }

            foreach (var page in pages)
            {
                var twin = page[..^".md".Length] + ".uk.md";
                if (!File.Exists(twin) || !links.All(link => File.ReadAllText(twin).Contains(link, StringComparison.Ordinal)))
                {
                    problems.Add($"docs/{Relative(page)} shows {shot}, but docs/{Relative(twin)} does not link both {links[0]} and {links[1]}");
                }
            }
        }

        var known = Shots.SelectMany(Files).ToHashSet(StringComparer.Ordinal);
        if (Directory.Exists(Images))
        {
            problems.AddRange(Directory.EnumerateFiles(Images).Select(Path.GetFileName).OfType<string>().Where(file => !known.Contains(file))
                .Select(file => $"docs/images/{file} is no file of a shot in {nameof(ScreenshotTests)}.{nameof(Shots)}"));
        }

        if (problems.Count > 0)
        {
            Assert.Fail($"{problems.Count} problem(s) with the screenshots:\n{string.Join('\n', problems)}");
        }
    }

    /// <summary>
    /// Draws every shot in both languages and both themes into <c>docs/images/</c>. A file is written only when its
    /// pixels changed, so a second run on the same machine leaves git clean. Run it on Windows before each release.
    /// </summary>
    [Fact]
    public Task Generate()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(Variable) == "1",
            $"Set {Variable}=1 to draw docs/images/; run it on Windows, whose fonts the shots show.");
        return fx.UiAsync(async () =>
        {
            Directory.CreateDirectory(Images);
            var written = new List<string>();
            try
            {
                foreach (var (code, formats) in Languages)
                {
                    using var culture = new CultureScope(code, formats);
                    using var settings = new SettingsWorld(code);
                    foreach (var theme in Themes)
                    {
                        Application.Current!.RequestedThemeVariant = theme;
                        foreach (var shot in Shots)
                        {
                            var pixels = await RenderAsync(shot, settings);
                            var file = FileName(shot, code, theme);
                            if (Save(pixels, Path.Combine(Images, file)))
                            {
                                written.Add(file);
                            }
                        }
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }

            TestContext.Current.TestOutputHelper?.WriteLine(written.Count == 0
                ? "docs/images/: no shot changed"
                : $"docs/images/: {written.Count} file(s) written: {string.Join(", ", written)}");
        });
    }

    private static Task<Bitmap> RenderAsync(string shot, SettingsWorld settings) => shot switch
    {
        _ when SettingsShots.FirstOrDefault(s => s.Name == shot) is { } page => SettingsAsync(settings, page),
        "setup-languages" => SetupAsync(Step.Languages),
        "setup-model" => SetupAsync(Step.Model),
        "setup-cleanup" => SetupAsync(Step.Cleanup),
        "setup-words" => SetupAsync(Step.Words),
        "setup-login" => SetupAsync(Step.Login),
        "setup-downloads" => SetupAsync(Step.Downloads),
        "setup-downloading" => SetupAsync(Step.Downloading),
        "setup-ready" => SetupAsync(Step.Ready),
        _ when shot == OverlayShot(OverlayPhase.Listening) => Task.FromResult<Bitmap>(Overlay(OverlayPhase.Listening)),
        _ when shot == OverlayShot(OverlayPhase.Transcribing) => Task.FromResult<Bitmap>(Overlay(OverlayPhase.Transcribing)),
        _ => throw new InvalidOperationException($"No way to draw the shot {shot}"),
    };

    // ---- Settings ----

    /// <summary>The Settings window at its designed size, on the shot's page.</summary>
    private static async Task<Bitmap> SettingsAsync(SettingsWorld world, SettingsShot shot)
    {
        using var shown = await world.ShowAsync(shot);
        return await CaptureAsync(shown.Window, world.Root);
    }

    /// <summary>A Settings shot: its name in <c>docs/images/</c>, the page, and what is done on the page before the shot.</summary>
    internal sealed record SettingsShot(string Name, SettingsPageKey Key, Action<SettingsPage>? Arrange = null);

    /// <summary>A Settings window that a <see cref="SettingsWorld"/> shows; disposing it closes the window.</summary>
    internal sealed class ShownSettings(SettingsWindow window, SettingsViewModel model) : IDisposable
    {
        public SettingsWindow Window => window;

        public void Dispose()
        {
            window.Close();
            model.Dispose();
        }
    }

    /// <summary>
    /// The config, the files and the fakes the Settings pages show: one installed model, three dictations, every check OK.
    /// Without <c>speechModel</c> the speech model is not installed, which spares the temp folder its 870 MB.
    /// </summary>
    internal sealed class SettingsWorld : IDisposable
    {
        private const int MeterTicks = 12;

        /// <summary>About −31 dBFS: ordinary speech, 17 of the meter's 30 segments.</summary>
        private const float SpeechRms = 0.028f;

        private readonly TempDirectory _dir = new();
        private readonly ManualClock _clock = new(Today(10, 2));
        private readonly FrameHub _frames;
        private UpdateChecker? _updates;

        public SettingsWorld(string language, bool speechModel = true)
        {
            Paths = new AppPaths(Path.Combine(Root, "Roaming", "Dictum"), Path.Combine(Root, "Local", "Dictum"));
            Directory.CreateDirectory(Paths.ConfigDirectory);
            File.Copy(Path.Combine(Repository.Root, "tests", "Dictum.App.Tests", "Docs", "docs-config.json"), Paths.ConfigFile);
            if (speechModel)
            {
                Install(Paths, Catalog.BuiltIn.Find(ModelsPageTests.Turbo)!);
            }

            Install(Paths, Catalog.BuiltIn.Find(SetupPlanner.VadId)!);
            WriteHistory(Paths.HistoryFile, language);
            _frames = new FrameHub(_clock);
            Channel = new FakeChannel { Link = Linked() };
            Channel.Answer = Answer;
            Status = new StatusViewModel(Display(Paths.ConfigFile), Display(Path.Combine(Paths.LogsDirectory, AppPaths.GuiLogName)), () => { });
            Status.OnLink(Channel.Link);
        }

        public string Root => _dir.Path;

        public AppPaths Paths { get; }

        public FakeChannel Channel { get; }

        public StatusViewModel Status { get; }

        public FakeRunner Runner { get; } = new();

        /// <summary>
        /// The Settings window on the shot's page, set up and settled, at <paramref name="size"/> or at the window's
        /// designed size.
        /// </summary>
        public async Task<ShownSettings> ShowAsync(SettingsShot shot, Size? size = null)
        {
            var timers = new ManualTimers();
            var session = new SettingsSession(new ConfigStore(Paths.ConfigFile, NullLog.Instance), timers, NullLog.Instance);
            session.Load();
            var pages = Pages(session, timers);
            var model = new SettingsViewModel(session, pages, (_, _) => { });
            model.OnLink(Channel.Link);

            var window = new SettingsWindow { DataContext = model };
            if (size is { } chosen)
            {
                (window.Width, window.Height) = (chosen.Width, chosen.Height);
            }

            window.FindControl<TransitioningContentControl>("PageHost")!.PageTransition = null;
            Opaque(window);
            window.Show();
            model.Open(shot.Key);
            shot.Arrange?.Invoke(pages.Single(p => p.Key == shot.Key).Page);
            await SettleAsync(timers);
            return new ShownSettings(window, model);
        }

        /// <summary>The pages as the shell builds them, on fakes.</summary>
        public IReadOnlyList<(SettingsPageKey Key, SettingsPage Page)> Pages(SettingsSession session, ManualTimers timers)
        {
            var secrets = new FakeSecrets();
            var autostart = new FakeAutostart { Installed = LoginApp.DesktopApp };
            var general = new GeneralPage(session, new GeneralServices(
                Channel, _frames, timers, autostart, Executable, new FakeSounds(), _clock, NullLog.Instance, IsLinux: false));
            var profiles = new ProfilesPage(session, Channel);
            var providers = new SpeechProvidersPage(session, new SpeechServices(
                Channel, _frames, timers, secrets, current => WhisperModels.Choices(Paths, Catalog.BuiltIn, current),
                model => WhisperModels.IsPresent(Paths, model), _ => Task.CompletedTask, _clock, NullLog.Instance));
            var processors = new ProcessorsPage(session, new ProcessorsServices(Runner, secrets, new PromptLibrary(Paths.PromptsDirectory), timers, NullLog.Instance));
            var models = new ModelsPage(session, new ModelsServices(
                Paths, Catalog.BuiltIn, new FakeDownloads(), () => Task.FromResult(Hardware), () => Hardware.FreeDiskBytes, MemoryBytes, CpuCores,
                new SpeedMeasurements(), Channel, timers, _ => Task.CompletedTask, _clock, NullLog.Instance));
            var system = new SystemPage(new SystemServices(
                Channel, Status, () => OverlayAvailability.Row(OverlayHost.Windows, enabled: true),
                () => TrayVisibility.Row(TrayDesktop.Windows, available: true, hidden: false, openSettings: null),
                timers, _clock, "Windows 11", (_, _) => { }));
            var history = new HistoryPage(session, Paths.HistoryFile, new NoClipboard(), Runner, NullLog.Instance);
            var about = new AboutPage(new AboutServices(
                SettingsViewModel.AppVersion(), ShownSystem, "win-x64", null, () => Task.FromResult(Hardware), new NoClipboard(), (_, _) => { }, _ => { },
                NullLog.Instance, Updates()));
            return
            [
                (SettingsPageKey.General, general),
                (SettingsPageKey.Profiles, profiles),
                (SettingsPageKey.SpeechProviders, providers),
                (SettingsPageKey.Processors, processors),
                (SettingsPageKey.Models, models),
                (SettingsPageKey.System, system),
                (SettingsPageKey.History, history),
                (SettingsPageKey.About, about),
            ];
        }

        /// <summary>
        /// Runs the pages' timers as time passes: the microphone meter rises to ordinary speech, the processor's preview
        /// answers; then lets layout and the theme's transitions finish.
        /// </summary>
        public async Task SettleAsync(ManualTimers timers)
        {
            for (var i = 0; i < MeterTicks; i++)
            {
                _clock.Advance(GeneralPage.MeterTick);
                _frames.Publish(new IpcEvent(IpcEventKind.Frame, Rms: SpeechRms));
                timers.Fire();
                await Task.Yield();
                Dispatcher.UIThread.RunJobs();
            }

            Settle();
        }

        public string Display(string path) => DisplayPath(path, Root);

        public void Dispose()
        {
            _updates?.Dispose();
            _dir.Dispose();
        }

        /// <summary>The update check as About Dictum shows it: this version is the newest, checked this morning.</summary>
        private UpdateChecker Updates()
        {
            if (_updates is null)
            {
                var version = SettingsViewModel.AppVersion();
                var file = new UpdateCheckFile(Paths.UpdateCheckFile, NullLog.Instance);
                file.Save(new UpdateCheckState
                {
                    LastChecked = Today(9, 30), Latest = version, Page = FakeReleases.Page("v" + version).AbsoluteUri,
                });
                _updates = new UpdateChecker(new FakeReleases(), file, new ManualTimers(), _clock, version, NullLog.Instance);
            }

            return _updates;
        }

        private static DaemonLink Linked() => new(DaemonLinkState.Connected, new DaemonStatus(
            nameof(PipelineState.Idle), null,
            [new ProfileStatus("dictate", "Ctrl+Alt+Space", nameof(HotkeyMode.PushToTalk), true), new ProfileStatus("toggle", "Ctrl+Alt+D", nameof(HotkeyMode.Toggle), true)],
            HotkeyBackend, PasteBackend, DaemonPid, WarmedUp: true));

        private IpcResponse Answer(IpcRequest request) => request.Command switch
        {
            IpcCommand.Status => IpcResponse.Success(Linked().Status!),
            IpcCommand.Devices => new IpcResponse(true, Devices: [Headset, UsbMicrophone]),
            IpcCommand.Doctor => new IpcResponse(true, Checks: Checks()),
            _ => new IpcResponse(true),
        };

        /// <summary>What <c>dictum doctor</c> says on a Windows 11 computer where everything works.</summary>
        private List<PlatformCheck> Checks() =>
        [
            Ok(PlatformCheckNames.Config, $"{Display(Paths.ConfigFile)} (1 STT, 1 processors, 2 profiles)"),
            Ok(PlatformCheckNames.Models, $"{Display(Paths.ModelsDirectory)} (2 configured file(s) present)"),
            Ok(PlatformCheckNames.Gpu, HardwareText.Gpu(Hardware)!),
            Ok(PlatformCheckNames.AudioLibrary, "PortAudio V19.7.0-devel, revision 147dd722548358763a8b649b3e4b41dfffbcfbb6 (bundled portaudio.dll)"),
            Ok(PlatformCheckNames.Microphone, $"{Headset.Name} ({Headset.HostApi}, {Headset.DefaultSampleRate:F0} Hz); 2 input device(s)"),
            Ok(PlatformCheckNames.Hotkeys, HotkeyBackend),
            Ok(PlatformCheckNames.Paste, PasteBackend),
            Ok(PlatformCheckNames.Secrets, "Windows Credential Manager"),
            Ok(PlatformCheckNames.Daemon, $"running (pid {DaemonPid}), answering this check"),
            Ok(PlatformCheckNames.Stt("local"), $"{ModelsPageTests.TurboFile}, runtime Vulkan on {Hardware.GpuName}"),
            Ok(PlatformCheckNames.Processor("cleanup"), "http://localhost:8080/v1 reachable, model qwen3-4b, prompt 'cleanup'"),
        ];

        private static PlatformCheck Ok(string name, string detail) => new(name, HealthStatus.Ok(detail));
    }

    private sealed class NoClipboard : ITextClipboard
    {
        public Task SetTextAsync(string text) => Task.CompletedTask;
    }

    // ---- Setup wizard ----

    private enum Step
    {
        Languages,
        Model,
        Cleanup,
        Words,
        Login,
        Downloads,
        Downloading,
        Ready,
    }

    /// <summary>
    /// A first setup walked to <paramref name="step"/> in <c>SetupWindow</c>, with the answers the docs describe:
    /// Ukrainian and English, the recommended model, a clean-up server on this computer, a few words, start at login.
    /// </summary>
    private static async Task<Bitmap> SetupAsync(Step step)
    {
        using var dir = new TempDirectory();
        var paths = new AppPaths(Path.Combine(dir.Path, "Roaming", "Dictum"), Path.Combine(dir.Path, "Local", "Dictum"));
        Directory.CreateDirectory(paths.ConfigDirectory);
        var timers = new ManualTimers();
        var clock = new ManualClock(Today(10, 0));
        var downloads = new FakeDownloads();
        var daemon = new FakeChannel { Link = new DaemonLink(DaemonLinkState.NotConfigured) };
        var probe = new FakeProbe { Answer = new ServerCheck(true, string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SetupServerReachable), 12)) };
        var services = new SetupServices(
            paths, new ConfigStore(paths.ConfigFile, NullLog.Instance), Catalog.BuiltIn, downloads,
            () => Task.FromResult(Hardware), () => Hardware.FreeDiskBytes, MemoryBytes, CpuCores, new SpeedMeasurements(), probe, new FakeSecrets(),
            new FakeAutostart(), Executable, new TrayHint(TrayDesktop.Windows, () => true, () => { }),
            daemon, () => { }, (_, _) => { }, timers, clock, NullLog.Instance, new DownloadsLocation());
        var wizard = new SetupViewModel(services);

        var window = new SetupWindow { DataContext = wizard };
        Opaque(window);
        window.Show();
        wizard.OnOpened();
        Dispatcher.UIThread.RunJobs();

        // Step 1: Ukrainian and English, Ukrainian the main one. A Ukrainian system ticks both; on an English one the
        // user finds Ukrainian with the search, ticks it and makes it main.
        var ukrainian = wizard.Languages.Rows.Single(r => r.Code == "uk");
        if (!ukrainian.IsChecked)
        {
            wizard.Languages.Search = "Ukrainian";
            ukrainian.IsChecked = true;
            ukrainian.MakeMainCommand.Execute(null);
        }

        if (step > Step.Languages)
        {
            await wizard.NextCommand.ExecuteAsync(null);
            await wizard.Model.ProbeAsync();
        }

        if (step > Step.Model)
        {
            await wizard.NextCommand.ExecuteAsync(null);
            wizard.Cleanup.UseServer = true;
            wizard.Cleanup.Url = "http://localhost:8080/v1";
            wizard.Cleanup.Model = "qwen3-4b";
            await wizard.Cleanup.TestCommand.ExecuteAsync(null);
        }

        if (step > Step.Cleanup)
        {
            await wizard.NextCommand.ExecuteAsync(null);
            wizard.Words.Text = "Dictum\nWhisper\nllama.cpp";
        }

        while (wizard.Index < Math.Min((int)step, (int)Step.Downloads))
        {
            await wizard.NextCommand.ExecuteAsync(null);
        }

        if (step > Step.Downloads)
        {
            // Step 7: the voice activity model is in, the speech model is two fifths of the way at 18 MB/s.
            await wizard.NextCommand.ExecuteAsync(null);
            var vad = Catalog.BuiltIn.Find(SetupPlanner.VadId)!;
            var speech = Catalog.BuiltIn.Find(ModelsPageTests.Turbo)!;
            Install(paths, vad);
            downloads.Set(new QueuedDownload(vad, DownloadState.Done, vad.Size));
            downloads.Set(new QueuedDownload(speech, DownloadState.Downloading, DownloadedBytes));
            clock.Advance(TimeSpan.FromSeconds(2));
            downloads.Set(new QueuedDownload(speech, DownloadState.Downloading, DownloadedBytes + 2 * DownloadRate));
        }

        if (step > Step.Downloading)
        {
            // Step 8: the files are in, the wizard saved, and the daemon it started runs.
            var speech = Catalog.BuiltIn.Find(ModelsPageTests.Turbo)!;
            Install(paths, speech);
            downloads.Set(new QueuedDownload(speech, DownloadState.Done, speech.Size));
            await wizard.NextCommand.ExecuteAsync(null);
            var profile = new ConfigStore(paths.ConfigFile, NullLog.Instance).Load().Profiles[0];
            daemon.Link = new DaemonLink(DaemonLinkState.Connected, new DaemonStatus(
                nameof(PipelineState.Idle), null, [new ProfileStatus(profile.Id, profile.Hotkey, nameof(HotkeyMode.PushToTalk), true)],
                HotkeyBackend, PasteBackend, DaemonPid, WarmedUp: true));
            wizard.OnLink(daemon.Link);
        }

        Assert.Equal((int)step, wizard.Index);
        Settle();
        var frame = await CaptureAsync(window, dir.Path);
        window.Close();
        wizard.OnClosed();
        return frame;
    }

    // ---- Overlay ----

    /// <summary>
    /// One moment of the overlay, 480 × 96 at medium size: mid-speech while listening, the line settled while
    /// transcribing. It floats over whatever is on screen; the shot puts it on the dark desktop that the app's own
    /// preview (<see cref="OverlayPreview"/>) draws, in both themes, so that its light caption reads on a light page too.
    /// </summary>
    private static RenderTargetBitmap Overlay(OverlayPhase phase)
    {
        var scene = phase == OverlayPhase.Listening
            ? new OverlayScene(phase, 1.2, 3.4, 1, 0.55, false, [0.62, 0.5, 0.41, 0.3, 0.2], "UK/EN", null, false, OverlayPalette.Aurora)
            : new OverlayScene(phase, 0.9, 4.1, 1, 0, false, [0, 0, 0, 0, 0], null, null, false, OverlayPalette.Aurora);
        var host = new Border { Background = new SolidColorBrush(Color.Parse(DesktopColour)), Child = new OverlayCanvas { Scene = scene } };
        var window = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var bitmap = new RenderTargetBitmap(new PixelSize((int)OverlayTokens.Width, (int)OverlayTokens.Height));
        bitmap.Render(host);
        window.Close();
        return bitmap;
    }

    private static string DisplayPath(string path, string root) => path.Replace(root, ShownAppData, StringComparison.OrdinalIgnoreCase);

    /// <summary>A model file of the catalog's size, which is what counts as installed; its bytes are never read.</summary>
    private static void Install(AppPaths paths, CatalogEntry entry)
    {
        Directory.CreateDirectory(paths.ModelsDirectory);
        using var file = File.Create(paths.ResolveModel(entry.File));
        file.SetLength(entry.Size);
    }

    /// <summary>A morning on the day the shots show, at that hour on this computer's clock, whatever its time zone.</summary>
    private static DateTimeOffset Today(int hour, int minute)
    {
        var local = new DateTime(2026, 10, 20, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>Three dictations, newest last, in the shot's language: two through the LLM clean-up, one toggled.</summary>
    private static void WriteHistory(string file, string language)
    {
        var history = new HistoryStore(file, 200);
        var entries = language == "uk"
            ? new[]
            {
                (Today(8, 52), "dictate", "нагадай мені завантажити модель і на ноутбук", "Нагадай мені завантажити модель і на ноутбук.", 3.1),
                (Today(9, 15), "toggle", "Нотатки до випуску: майстер налаштування тепер перевіряє сервер, перш ніж зберегти", "Нотатки до випуску: майстер налаштування тепер перевіряє сервер, перш ніж зберегти", 14.8),
                (Today(9, 41), "dictate", "зустріч перенесли на четвер на десяту можеш оновити запрошення", "Зустріч перенесли на четвер на десяту. Можеш оновити запрошення?", 6.2),
            }
            : new[]
            {
                (Today(8, 52), "dictate", "remind me to download the model on the laptop too", "Remind me to download the model on the laptop too.", 3.1),
                (Today(9, 15), "toggle", "Release notes: the setup wizard now checks the server before it saves", "Release notes: the setup wizard now checks the server before it saves", 14.8),
                (Today(9, 41), "dictate", "so the meeting moved to thursday at ten can you update the invite", "The meeting moved to Thursday at ten. Can you update the invite?", 6.2),
            };
        foreach (var (at, profile, raw, text, seconds) in entries)
        {
            history.Append(new HistoryEntry(at, profile, "local", raw, text, language, seconds,
                Model: ModelsPageTests.TurboFile, Backend: "vulkan", TranscribeMs: (long)(seconds * 110)));
        }
    }

    private static IBrush Base(ThemeVariant theme) =>
        Application.Current!.TryGetResource("color.bg.base", theme, out var brush) && brush is IBrush solid ? solid : Brushes.Transparent;

    /// <summary>The window colour behind the content: on Windows 11 the app asks for Mica, which a headless window does not draw.</summary>
    private static void Opaque(Window window) => window.Background = Base(Application.Current!.ActualThemeVariant);

    /// <summary>Runs the queued work and lets layout and the theme's transitions finish.</summary>
    private static void Settle()
    {
        for (var i = 0; i < SettleFrames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(FrameMilliseconds);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The window's pixels, the same on every run: no caret, every list and page scrolled to its top, the overlay
    /// previews stopped at one moment, and the temp folder's paths shown as a user's. Fails if any text still names the
    /// temp folder or the user's profile.
    /// </summary>
    private static async Task<Bitmap> CaptureAsync(Window window, string root)
    {
        TopLevel.GetTopLevel(window)!.FocusManager!.Focus(null);
        foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>())
        {
            scroll.Offset = default;
        }

        foreach (var preview in window.GetVisualDescendants().OfType<OverlayPreview>())
        {
            Freeze(preview);
        }

        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text?.Contains(root, StringComparison.OrdinalIgnoreCase) == true))
        {
            text.Text = DisplayPath(text.Text!, root);
        }

        foreach (var box in window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Text?.Contains(root, StringComparison.OrdinalIgnoreCase) == true))
        {
            box.Text = DisplayPath(box.Text!, root);
        }

        Settle();
        await Task.Yield();
        string[] secret = [Path.GetTempPath().TrimEnd('\\'), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
        var leaks = window.GetVisualDescendants()
            .Select(v => v switch { TextBlock t => t.Text, TextBox b => b.Text, _ => null })
            .OfType<string>()
            .Select(text => text.Replace("\u200B", "", StringComparison.Ordinal))
            .Where(text => secret.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(leaks.Count == 0, $"A shot would show a local path: {string.Join(" | ", leaks)}");

        return window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The window rendered no frame");
    }

    /// <summary>The preview stops following the clock and shows one moment of listening, so each run draws the same pixels.</summary>
    private static void Freeze(OverlayPreview preview)
    {
        Running(preview) = false;
        preview.Canvas.Scene = new OverlayScene(OverlayPhase.Listening, 1.2, 3.4, 1, 0.55, false, [0.62, 0.5, 0.41, 0.3, 0.2], null, null, false, preview.Palette);
        preview.Canvas.LayerOpacity = preview.OverlayOpacity;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_running")]
    private static extern ref bool Running(OverlayPreview preview);

    /// <summary>
    /// Writes <paramref name="bitmap"/> as a PNG unless the file there has the same pixels: both are compared after a
    /// PNG round trip, so an encoder that writes other bytes for the same pixels changes nothing either.
    /// </summary>
    private static bool Save(Bitmap bitmap, string file)
    {
        using (bitmap)
        {
            using var encoded = new MemoryStream();
            bitmap.Save(encoded, Png);
            if (File.Exists(file))
            {
                encoded.Position = 0;
                using var fresh = new Bitmap(encoded);
                using var existing = new Bitmap(file);
                if (fresh.PixelSize == existing.PixelSize && Pixels.Of(fresh).AsSpan().SequenceEqual(Pixels.Of(existing)))
                {
                    return false;
                }
            }

            File.WriteAllBytes(file, encoded.ToArray());
            return true;
        }
    }

    private static string Relative(string path) => Path.GetRelativePath(Docs, path).Replace('\\', '/');

    /// <summary>A UI language and its formats, on this thread and on threads started later, until disposed.</summary>
    internal sealed class CultureScope : IDisposable
    {
        private readonly (CultureInfo Ui, CultureInfo Formats, CultureInfo? DefaultUi, CultureInfo? DefaultFormats) _saved =
            (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture, CultureInfo.DefaultThreadCurrentUICulture, CultureInfo.DefaultThreadCurrentCulture);

        public CultureScope(string code, string formats) =>
            Apply(CultureInfo.GetCultureInfo(code), CultureInfo.GetCultureInfo(formats), CultureInfo.GetCultureInfo(code), CultureInfo.GetCultureInfo(formats));

        public void Dispose() => Apply(_saved.Ui, _saved.Formats, _saved.DefaultUi, _saved.DefaultFormats);

        private static void Apply(CultureInfo ui, CultureInfo formats, CultureInfo? defaultUi, CultureInfo? defaultFormats)
        {
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.CurrentCulture = formats;
            CultureInfo.DefaultThreadCurrentUICulture = defaultUi;
            CultureInfo.DefaultThreadCurrentCulture = defaultFormats;
        }
    }
}
