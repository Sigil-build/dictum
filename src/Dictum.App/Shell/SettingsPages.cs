using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Processing;

namespace Dictum.App.Shell;

/// <summary>The seven pages of a Settings window, over one session of the config file. The self-test builds them the same way.</summary>
internal static class SettingsPages
{
    /// <param name="clipboard">The window's clipboard, for History's copy.</param>
    /// <param name="runSetup">General → "Run setup again".</param>
    public static IReadOnlyList<(SettingsPageKey Key, SettingsPage Page)> Create(
        SettingsSession session, ShellServices services, ITextClipboard clipboard, Action runSetup)
    {
        var general = new GeneralPage(session, new GeneralServices(
            services.Daemon, services.Frames, services.Timers, services.Autostart, services.Executable, services.Sounds, services.Time, services.Log,
            services.Desktop.IsLinux));
        general.SetupRequested += runSetup;
        var profiles = new ProfilesPage(session, services.Daemon);
        var secrets = services.Secrets();
        var providers = new SpeechProvidersPage(session, new SpeechServices(
            services.Daemon, services.Frames, services.Timers, secrets,
            current => WhisperModels.Choices(services.Paths, services.Catalog, current), model => WhisperModels.IsPresent(services.Paths, model),
            services.Delay, services.Time, services.Log));
        var system = new SystemPage(new SystemServices(
            services.Daemon, services.Status, () => services.Integration.OverlayRow(session.Config.General.Overlay.Enabled), services.Integration.TrayRow,
            services.Timers, services.Time, services.Integration.Name, services.OpenDocs));
        var runner = services.Runner();
        var processors = new ProcessorsPage(session, new ProcessorsServices(
            runner, secrets, new PromptLibrary(services.Paths.PromptsDirectory), services.Timers, services.Log));
        var models = new ModelsPage(session, new ModelsServices(
            services.Paths, services.Catalog, services.Downloads(), services.Hardware, services.FreeDiskBytes,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount, services.Measurements, services.Daemon, services.Timers,
            services.Delay, services.Time, services.Log));
        var history = new HistoryPage(session, services.Paths.HistoryFile, clipboard, runner, services.Log);
        return
        [
            (SettingsPageKey.General, general),
            (SettingsPageKey.Profiles, profiles),
            (SettingsPageKey.SpeechProviders, providers),
            (SettingsPageKey.Processors, processors),
            (SettingsPageKey.Models, models),
            (SettingsPageKey.System, system),
            (SettingsPageKey.History, history),
        ];
    }
}
