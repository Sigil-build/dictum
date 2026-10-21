using Dictum.App.Daemon;
using Dictum.App.Settings;
using Dictum.App.Shell;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Setup;

/// <summary>What the setup wizard uses; the shell's own pieces in the app, fakes in tests.</summary>
/// <param name="Hardware">The shell's one GPU probe, shared with Settings → Models.</param>
/// <param name="FreeDiskBytes">Free space where the models go; <see cref="long.MaxValue"/> when unknown.</param>
/// <param name="MemoryBytes">The computer's memory, for the GPU and memory line.</param>
/// <param name="CpuCores">For the CPU speed anchors.</param>
/// <param name="Autostart">The OS login item; null where there is none.</param>
/// <param name="Executable">The running <c>dictum-gui</c>, which the login item starts; null when unknown.</param>
/// <param name="Daemon">The daemon link, for step 8's live line.</param>
/// <param name="RestartDaemon">Step 8's Restart: the supervisor's, as on the System page.</param>
/// <param name="OpenDocs">Step 1's "How does Dictum work?".</param>
public sealed record SetupServices(
    AppPaths Paths,
    ConfigStore Store,
    Catalog Catalog,
    IModelDownloads Downloads,
    Func<Task<HardwareInfo>> Hardware,
    Func<long> FreeDiskBytes,
    long MemoryBytes,
    int CpuCores,
    SpeedMeasurements Measurements,
    IServerProbe Server,
    ISecretStore Secrets,
    IAutostart? Autostart,
    string? Executable,
    TrayHint Tray,
    IDaemonChannel Daemon,
    Action RestartDaemon,
    OpenDocs OpenDocs,
    ITimers Timers,
    TimeProvider Time,
    ILog Log);

/// <summary>What step 8 says about the tray icon.</summary>
/// <param name="Hidden">Whether the icon is hidden: true, false, or null when unknown.</param>
/// <param name="OpenSettings">Opens the OS page that pins it; null where there is none (KDE, others).</param>
public sealed record TrayHint(TrayDesktop Desktop, Func<bool?> Hidden, Action? OpenSettings);
