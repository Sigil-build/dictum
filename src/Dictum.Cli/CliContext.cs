using System.CommandLine;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.Cli;

/// <summary>Per-invocation state: paths, logging, config, the platform and a lazily built host.</summary>
internal sealed class CliContext : IAsyncDisposable
{
    private DictumHost? _host;
    private DictumConfig? _config;
    private bool _configLoaded;
    private PlatformServices? _platform;

    /// <param name="consoleLevel">Overrides the stderr log level (the daemon logs Info by default).</param>
    public CliContext(ParseResult parse, LogLevel? consoleLevel = null)
    {
        Out = Console.Out;
        Error = Console.Error;
        Paths = AppPaths.FromEnvironment();
        Verbose = parse.GetValue(CliApp.VerboseOption);
        ConfigPath = Paths.ConfigFileFrom(parse.GetValue(CliApp.ConfigOption));
        Log = new DictumLog(Verbose ? LogLevel.Debug : consoleLevel ?? LogLevel.Warning, includeText: Verbose, Error, Paths.LogsDirectory);
        NativeStderr.ShowDiagnostics = Verbose;
        Store = new ConfigStore(ConfigPath, Log);
    }

    public AppPaths Paths { get; }

    public bool Verbose { get; }

    public string ConfigPath { get; }

    public DictumLog Log { get; }

    public ConfigStore Store { get; }

    public TextWriter Out { get; }

    public TextWriter Error { get; }

    public string SocketPath => IpcEndpoint.SocketPath(Paths);

    /// <summary>Platform services; settings are re-read from the current config at use time.</summary>
    public PlatformServices Platform => _platform ??= PlatformFactory.Create(
        Log,
        () =>
        {
            var general = _config?.General ?? new GeneralSettings();
            return new PlatformSettings(general.TerminalApps, general.Injector);
        },
        Paths.DataDirectory);

    /// <summary>The config, or null when the file does not exist. Invalid files throw <see cref="ConfigException"/>.</summary>
    public DictumConfig? TryLoadConfig()
    {
        if (!_configLoaded)
        {
            if (Store.Exists)
            {
                var (config, warnings) = Store.LoadWithIssues();
                foreach (var warning in warnings)
                {
                    Log.Warn($"config: {warning}");
                }

                _config = config;
            }

            _configLoaded = true;
        }

        return _config;
    }

    public DictumConfig LoadConfig() => TryLoadConfig() ?? Store.Load();

    /// <summary>Saves an edited config, refusing an edit that makes it invalid.</summary>
    public void SaveValidated(DictumConfig config)
    {
        var errors = ConfigValidator.Validate(config).Where(i => i.Severity == ConfigIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new ConfigException("The change would make the config invalid.", errors);
        }

        Store.Save(config);
    }

    /// <summary>Replaces the config seen by later platform calls (the daemon updates it on reload).</summary>
    public void UseConfig(DictumConfig config)
    {
        _config = config;
        _configLoaded = true;
    }

    public DictumHost Host() => _host ??= CreateHost(TryLoadConfig());

    public DictumHost CreateHost(DictumConfig? config) => DictumHost.Create(Paths, Log, config, Platform.Secrets);

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }

        Log.Dispose();
    }
}
