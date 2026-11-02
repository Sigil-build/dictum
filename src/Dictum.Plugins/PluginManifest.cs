using System.Globalization;
using System.Text.Json;
using Dictum.Core;
using Dictum.Core.Configuration;

namespace Dictum.Plugins;

/// <summary>A plugin's <c>plugin.json</c>: who it is, the program to run for each system, and the models it offers.</summary>
public sealed record PluginManifest(
    int Protocol,
    string Id,
    string Name,
    string Version,
    string? Publisher,
    string? Homepage,
    IReadOnlyDictionary<string, string> Executable,
    IReadOnlyList<PluginManifestModel>? Models)
{
    private const int Sha256HexLength = 64;

    /// <summary>Reads and checks <c>plugin.json</c> in the folder; every problem is collected before it throws.</summary>
    /// <exception cref="PluginManifestException">The file is missing, is not JSON, or breaks a rule.</exception>
    public static PluginManifest Load(string folder)
    {
        var path = Path.Combine(folder, PluginProtocol.ManifestFile);
        if (!File.Exists(path))
        {
            throw new PluginManifestException(folder, [$"There is no {PluginProtocol.ManifestFile} in {folder}."]);
        }

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(File.ReadAllText(path), PluginJsonContext.Default.PluginManifest);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new PluginManifestException(folder, [$"{PluginProtocol.ManifestFile} is not valid JSON: {ex.Message}"]);
        }

        if (manifest is null)
        {
            throw new PluginManifestException(folder, [$"{PluginProtocol.ManifestFile} is not valid JSON: it holds no object."]);
        }

        var problems = manifest.Check(folder);
        if (problems.Count > 0)
        {
            throw new PluginManifestException(folder, problems);
        }

        return manifest;
    }

    /// <summary>The full path of this system's program inside the plugin's folder.</summary>
    public string ExecutablePath(string folder) => Path.Combine(folder, Executable[PluginProtocol.CurrentRid]);

    private List<string> Check(string folder)
    {
        var problems = new List<string>();
        if (Protocol != PluginProtocol.Version)
        {
            // A future protocol may change any other field, so none of the rest is worth judging.
            problems.Add($"it needs protocol {Protocol.ToString(CultureInfo.InvariantCulture)}; this Dictum speaks protocol {PluginProtocol.Version}");
            return problems;
        }

        if (!PluginIds.IsValid(Id))
        {
            problems.Add($"id '{Id}' {PluginIds.Rule}");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("name must not be empty");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            problems.Add("version must not be empty");
        }

        CheckProgram(folder, problems);
        CheckModels(problems);
        return problems;
    }

    private void CheckProgram(string folder, List<string> problems)
    {
        var rid = PluginProtocol.CurrentRid;
        if (Executable is null || !Executable.TryGetValue(rid, out var file) || string.IsNullOrWhiteSpace(file))
        {
            problems.Add($"it has no program for {rid}");
            return;
        }

        if (!IsInside(folder, file))
        {
            problems.Add($"the program for {rid} must be a file name inside the folder");
        }
        else if (!File.Exists(Path.Combine(folder, file)))
        {
            problems.Add($"the program {file} for {rid} is not in the folder");
        }
    }

    private static bool IsInside(string folder, string file)
    {
        if (Path.IsPathRooted(file))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(Path.Combine(folder, file)).StartsWith(root, PathComparison.Current);
    }

    private void CheckModels(List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in Models ?? [])
        {
            if (string.IsNullOrWhiteSpace(model.Id))
            {
                problems.Add("a model has no id");
                continue;
            }

            if (!seen.Add(model.Id))
            {
                problems.Add($"model id '{model.Id}' appears twice");
            }

            if (model.Languages is null || model.Languages.Count == 0)
            {
                problems.Add($"model {model.Id} must list its languages");
            }

            foreach (var file in model.Files ?? [])
            {
                CheckModelFile(model.Id, file, problems);
            }
        }
    }

    private static void CheckModelFile(string modelId, PluginManifestFile file, List<string> problems)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"model {modelId}: {file.File} must come from an https address");
        }

        if (file.Sha256 is not { Length: Sha256HexLength } hash || !hash.All(char.IsAsciiHexDigit))
        {
            problems.Add($"model {modelId}: {file.File} needs a SHA-256");
        }

        if (file.Size <= 0)
        {
            problems.Add($"model {modelId}: {file.File} needs its size in bytes");
        }
    }
}

/// <summary>A model a plugin offers; Dictum downloads its files into the models folder for the plugin to load.</summary>
public sealed record PluginManifestModel(
    string Id,
    string Title,
    IReadOnlyList<PluginManifestFile> Files,
    IReadOnlyList<string> Languages,
    bool SetLanguage = true,
    bool Prompt = true,
    PluginManifestLicence? Licence = null);

public sealed record PluginManifestFile(string File, string Url, string Sha256, long Size);

public sealed record PluginManifestLicence(string Name, string? Url);

/// <summary>A plugin folder whose <c>plugin.json</c> cannot be used; <see cref="Problems"/> lists every fault found.</summary>
public sealed class PluginManifestException(string folder, IReadOnlyList<string> problems)
    : Exception($"{PluginProtocol.ManifestFile} in {folder} is not valid: {string.Join("; ", problems)}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
