namespace Dictum.Core;

/// <summary>Per-OS locations for config, data, downloads, history and logs.</summary>
/// <remarks>
/// Environment overrides: <see cref="DictumEnvironment.ConfigDirectory"/>, <see cref="DictumEnvironment.DataDirectory"/>.
/// The downloads folder, which holds the models, is <c>general.downloadsDirectory</c> from config.json, read on every use
/// so that a long-lived holder follows a change; without one it is the data folder.
/// </remarks>
public sealed class AppPaths
{
    private const string AppFolder = "Dictum";

    /// <summary>Folders under the XDG base directories are lower case by convention.</summary>
    private const string XdgAppFolder = "dictum";

    private const string MacApplicationSupport = "Application Support";

    /// <summary>The log of the CLI and the daemon it runs, in <see cref="LogsDirectory"/>.</summary>
    public const string DaemonLogName = "dictum.log";

    /// <summary>The desktop app's log, which also takes the output of a daemon the app started.</summary>
    public const string GuiLogName = "dictum-gui.log";

    private readonly Func<string?>? _downloadsDirectory;

    /// <param name="downloadsDirectory">The configured downloads folder, asked on every use; null or blank means the data folder.</param>
    public AppPaths(string configDirectory, string dataDirectory, Func<string?>? downloadsDirectory = null)
    {
        ConfigDirectory = configDirectory;
        DataDirectory = dataDirectory;
        _downloadsDirectory = downloadsDirectory;
    }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public string PromptsDirectory => Path.Combine(ConfigDirectory, "prompts");

    /// <summary>The downloads folder the config names, or null when it names none and the data folder is used.</summary>
    public string? ConfiguredDownloadsDirectory => _downloadsDirectory?.Invoke() is { } folder && !string.IsNullOrWhiteSpace(folder) ? folder : null;

    /// <summary>Where everything Dictum downloads goes: <see cref="ConfiguredDownloadsDirectory"/>, else the data folder.</summary>
    public string DownloadsDirectory => ConfiguredDownloadsDirectory ?? DataDirectory;

    /// <summary>A configured downloads folder that does not exist, e.g. on an unplugged drive; the data folder is never missing.</summary>
    public bool IsDownloadsDirectoryMissing => ConfiguredDownloadsDirectory is { } folder && !Directory.Exists(folder);

    public string ModelsDirectory => Path.Combine(DownloadsDirectory, "models");

    public string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    /// <summary>Downloads that dictum-gui has not finished yet, so a restart continues them.</summary>
    public string DownloadsFile => Path.Combine(DataDirectory, "downloads.json");

    /// <summary>The setup wizard's answers while its files download, so a restart returns to the walk.</summary>
    public string SetupFile => Path.Combine(DataDirectory, "setup.json");

    /// <summary>When dictum-gui last asked GitHub for the newest release, what it found and which version it last announced.</summary>
    public string UpdateCheckFile => Path.Combine(DataDirectory, "update-check.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>The tray glyphs as SVG files, for a StatusNotifierItem's icon theme path.</summary>
    public string TrayIconsDirectory => Path.Combine(DataDirectory, "tray-icons");

    /// <summary>The config file to use: <paramref name="explicitPath"/> (<c>--config</c>), then <see cref="DictumEnvironment.ConfigFile"/>, then <see cref="ConfigFile"/>.</summary>
    public string ConfigFileFrom(string? explicitPath) =>
        explicitPath ?? (Environment.GetEnvironmentVariable(DictumEnvironment.ConfigFile) is { Length: > 0 } fromEnvironment ? fromEnvironment : ConfigFile);

    /// <summary>Resolves a model reference: absolute paths are kept, bare names live in <see cref="ModelsDirectory"/>.</summary>
    public string ResolveModel(string model) =>
        Path.IsPathFullyQualified(model) ? model : Path.Combine(ModelsDirectory, model);

    /// <summary>The same config and data folders with <paramref name="directory"/> as the downloads folder.</summary>
    public AppPaths WithDownloads(string? directory) => new(ConfigDirectory, DataDirectory, () => directory);

    /// <summary>The same config and data folders with a downloads folder asked of <paramref name="directory"/> on every use.</summary>
    public AppPaths WithDownloads(Func<string?> directory) => new(ConfigDirectory, DataDirectory, directory);

    public static AppPaths FromEnvironment() =>
        new(
            Environment.GetEnvironmentVariable(DictumEnvironment.ConfigDirectory) is { Length: > 0 } config ? config : DefaultConfigDirectory(),
            Environment.GetEnvironmentVariable(DictumEnvironment.DataDirectory) is { Length: > 0 } data ? data : DefaultDataDirectory());

    private static string DefaultConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Home(), "Library", MacApplicationSupport, AppFolder);
        }

        return Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), XdgAppFolder);
    }

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Home(), "Library", MacApplicationSupport, AppFolder, "Data");
        }

        return Path.Combine(XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share")), XdgAppFolder);
    }

    private static string XdgDirectory(string variable, string fallbackUnderHome) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(Home(), fallbackUnderHome);

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
