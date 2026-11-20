using Dictum.App.Daemon;
using Dictum.App.Settings;
using Dictum.App.Updates;
using Dictum.App.ViewModels;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform;
using Dictum.Setup;
using Dictum.Updates;

namespace Dictum.App.Shell;

/// <summary>
/// What the shell shares with the Settings pages and the setup wizard. What the app opens on first use (the secret
/// store, the processor runner) or starts later (the download queue) comes through a function.
/// </summary>
/// <param name="Autostart">The OS login item; null where there is none, and in tests.</param>
/// <param name="Executable">What a login item starts; null when unknown.</param>
/// <param name="Hardware">The one GPU probe, which Settings → Models and the wizard share.</param>
/// <param name="FreeDiskBytes">Free space where the models go; <see cref="long.MaxValue"/> when unknown.</param>
/// <param name="Delay">The waits for the daemon's reload before a test.</param>
/// <param name="OpenDocs">Opens the documentation in the browser: Settings' Help, System's "How to fix", the wizard's step 1.</param>
/// <param name="Updates">The update check that About Dictum shows; null where the shell has none (tests, the self-test).</param>
/// <param name="UpdateService">Downloads and installs an update from About Dictum; null in tests and the self-test.</param>
/// <param name="IsDictating">The daemon records or processes a dictation now.</param>
/// <param name="WhenIdle">Runs an action once no dictation runs (UI thread).</param>
/// <param name="Quit">The shell's Quit, after the installer took over.</param>
/// <param name="LastUpdate">What the update the last run started came to, read at start.</param>
/// <param name="UpdateRun">The update About Dictum started, which outlives the Settings window.</param>
/// <param name="Location">The downloads folder that <paramref name="Paths"/> follows; null in tests that leave it alone.</param>
internal sealed record ShellServices(
    AppPaths Paths,
    Catalog Catalog,
    IDaemonChannel Daemon,
    StatusViewModel Status,
    FrameHub Frames,
    ITimers Timers,
    IAutostart? Autostart,
    string? Executable,
    ISoundPlayer Sounds,
    Func<ISecretStore> Secrets,
    Func<IProcessorRunner> Runner,
    Func<IModelDownloads> Downloads,
    Func<Task<HardwareInfo>> Hardware,
    Func<long> FreeDiskBytes,
    SpeedMeasurements Measurements,
    DesktopEnvironment Desktop,
    DesktopIntegration Integration,
    Func<TimeSpan, Task> Delay,
    OpenDocs OpenDocs,
    TimeProvider Time,
    ILog Log,
    UpdateChecker? Updates = null,
    DownloadsLocation? Location = null,
    UpdateService? UpdateService = null,
    Func<bool>? IsDictating = null,
    Action<Action>? WhenIdle = null,
    Action? Quit = null,
    Func<UpdateReport?>? LastUpdate = null,
    UpdateRun? UpdateRun = null);
