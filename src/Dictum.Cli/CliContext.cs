using System.CommandLine;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host;

namespace Dictum.Cli;

/// <summary>Per-invocation state: paths, logging, config and a lazily built host.</summary>
internal sealed class CliContext : IAsyncDisposable
{
    private DictumHost? _host;
    private DictumConfig? _config;
    private bool _configLoaded;

    public CliContext(ParseResult parse)
    {
        Paths = AppPaths.FromEnvironment();
        Verbose = parse.GetValue(CliApp.VerboseOption);
        ConfigPath = parse.GetValue(CliApp.ConfigOption)
            ?? (Environment.GetEnvironmentVariable("DICTUM_CONFIG") is { Length: > 0 } fromEnv ? fromEnv : Paths.ConfigFile);
        Log = new DictumLog(Verbose ? LogLevel.Debug : LogLevel.Warning, includeText: Verbose, Console.Error, Paths.LogsDirectory);
        Store = new ConfigStore(ConfigPath, Log);
    }

    public AppPaths Paths { get; }

    public bool Verbose { get; }

    public string ConfigPath { get; }

    public DictumLog Log { get; }

    public ConfigStore Store { get; }

    public TextWriter Out => Console.Out;

    public TextWriter Error => Console.Error;

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

    public DictumHost Host() => _host ??= DictumHost.Create(Paths, Log, TryLoadConfig());

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }

        Log.Dispose();
    }
}
