namespace Dictum.Core;

/// <summary>Per-OS locations for config, data, models, history and logs.</summary>
/// <remarks>
/// Environment overrides: <c>DICTUM_CONFIG_DIR</c>, <c>DICTUM_DATA_DIR</c>.
/// </remarks>
public sealed class AppPaths
{
    public AppPaths(string configDirectory, string dataDirectory)
    {
        ConfigDirectory = configDirectory;
        DataDirectory = dataDirectory;
    }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public string PromptsDirectory => Path.Combine(ConfigDirectory, "prompts");

    public string ModelsDirectory => Path.Combine(DataDirectory, "models");

    public string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Resolves a model reference: absolute paths are kept, bare names live in <see cref="ModelsDirectory"/>.</summary>
    public string ResolveModel(string model) =>
        Path.IsPathFullyQualified(model) ? model : Path.Combine(ModelsDirectory, model);

    public static AppPaths FromEnvironment() =>
        new(
            Environment.GetEnvironmentVariable("DICTUM_CONFIG_DIR") is { Length: > 0 } config ? config : DefaultConfigDirectory(),
            Environment.GetEnvironmentVariable("DICTUM_DATA_DIR") is { Length: > 0 } data ? data : DefaultDataDirectory());

    private static string DefaultConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dictum");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Home(), "Library", "Application Support", "Dictum");
        }

        return Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), "dictum");
    }

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dictum");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Home(), "Library", "Application Support", "Dictum", "Data");
        }

        return Path.Combine(XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share")), "dictum");
    }

    private static string XdgDirectory(string variable, string fallbackUnderHome) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(Home(), fallbackUnderHome);

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
