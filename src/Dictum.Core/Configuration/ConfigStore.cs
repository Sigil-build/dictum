using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dictum.Core.Diagnostics;

namespace Dictum.Core.Configuration;

/// <summary>Upgrades a config document from <see cref="FromVersion"/> to <c>FromVersion + 1</c>.</summary>
public interface IConfigMigration
{
    int FromVersion { get; }

    void Apply(JsonObject root);
}

/// <summary>Reads, migrates, validates and writes <c>config.json</c>.</summary>
public sealed class ConfigStore
{
    private const string SchemaVersionKey = "schemaVersion";

    /// <summary>The version of a file without <c>schemaVersion</c>.</summary>
    private const int FirstSchemaVersion = 1;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions MigratedWriteOptions = new() { WriteIndented = true };

    private readonly IReadOnlyList<IConfigMigration> _migrations;
    private readonly int _currentVersion;
    private readonly ILog _log;

    public ConfigStore(string path, ILog log)
        : this(path, [], DictumConfig.CurrentSchemaVersion, log)
    {
    }

    public ConfigStore(string path, IReadOnlyList<IConfigMigration> migrations, int currentVersion, ILog log)
    {
        Path = path;
        _migrations = migrations;
        _currentVersion = currentVersion;
        _log = log;
    }

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>Loads the config; throws <see cref="ConfigException"/> when it is missing or has errors.</summary>
    public DictumConfig Load() => LoadWithIssues().Config;

    /// <summary>Loads the config and also returns validation warnings.</summary>
    public (DictumConfig Config, IReadOnlyList<ConfigIssue> Warnings) LoadWithIssues()
    {
        if (!Exists)
        {
            throw new ConfigException($"Config file not found: {Path}. Run 'dictum init' to set Dictum up.");
        }

        var text = File.ReadAllText(Path);
        text = MigrateIfNeeded(text);
        var config = Parse(text);

        var issues = ConfigValidator.Validate(config);
        var errors = issues.Where(i => i.Severity == ConfigIssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new ConfigException($"Config is invalid: {Path}", errors);
        }

        return (config, issues);
    }

    public void Save(DictumConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        WriteAtomically(Path, Serialize(config));
    }

    public static string Serialize(DictumConfig config) =>
        JsonSerializer.Serialize(config, ConfigJsonContext.Default.DictumConfig) + "\n";

    /// <summary>Parses config JSON without migrating or validating it.</summary>
    public static DictumConfig Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.DictumConfig)
                ?? throw new ConfigException("Config is empty.");
        }
        catch (JsonException ex)
        {
            throw FromJsonException(ex);
        }
    }

    private string MigrateIfNeeded(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw FromJsonException(ex);
        }

        if (node is not JsonObject root)
        {
            throw new ConfigException("Config must be a JSON object.", [new(ConfigIssueSeverity.Error, "", "Expected a JSON object.", 1)]);
        }

        var version = root[SchemaVersionKey] is JsonValue value && value.TryGetValue<int>(out var v) ? v : FirstSchemaVersion;
        if (version > _currentVersion)
        {
            throw new ConfigException(
                $"Config schema version {version} is newer than this build supports ({_currentVersion}). Update Dictum.");
        }

        if (version == _currentVersion)
        {
            return text;
        }

        var backup = $"{Path}.v{version}.{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.bak";
        File.Copy(Path, backup, overwrite: false);

        while (version < _currentVersion)
        {
            var migration = _migrations.SingleOrDefault(m => m.FromVersion == version)
                ?? throw new ConfigException($"No migration from config schema version {version}.");
            migration.Apply(root);
            version++;
            root[SchemaVersionKey] = version;
        }

        var migrated = root.ToJsonString(MigratedWriteOptions) + "\n";
        WriteAtomically(Path, migrated);
        _log.Info($"Migrated config to schema version {version}; backup saved to {backup}");
        return migrated;
    }

    private static ConfigException FromJsonException(JsonException ex)
    {
        long? line = ex.LineNumber is { } l ? l + 1 : null;
        var path = ex.Path is { Length: > 1 } p ? p.TrimStart('$', '.') : string.Empty;
        var message = FirstSentence(ex.Message);
        return new ConfigException(
            $"Config is not valid JSON for Dictum: {message}",
            [new ConfigIssue(ConfigIssueSeverity.Error, path, message, line)],
            ex);
    }

    // STJ messages append "Path: $.x | LineNumber: n | BytePositionInLine: m." which ConfigIssue already carries.
    private static string FirstSentence(string message)
    {
        var cut = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
