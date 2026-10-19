using Dictum.App.Daemon;
using Dictum.App.Settings;
using Dictum.App.ViewModels;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform;
using Dictum.Setup;

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
    TimeProvider Time,
    ILog Log);
