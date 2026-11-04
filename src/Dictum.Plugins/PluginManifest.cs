using System.Globalization;
using System.Text.Json;
using Dictum.Core;
using Dictum.Core.Configuration;

namespace Dictum.Plugins;

/// <summary>A plugin's <c>plugin.json</c>: who it is, the program to run for each system, and the models it offers.</summary>
/// <param name="Program">
/// The files and folders the program is made of, relative to the plugin's folder, such as the script a launcher runs.
/// The trust record covers them, with <c>plugin.json</c> and the executable.
/// </param>
public sealed record PluginManifest(
    int Protocol,
    string Id,
    string Name,
    string Version,
    string? Publisher,
    string? Homepage,
    IReadOnlyDictionary<string, string> Executable,
    IReadOnlyList<PluginManifestModel>? Models,
    IReadOnlyList<string>? Program)
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
            manifest = Parse(File.ReadAllBytes(path));
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException(folder, [$"{PluginProtocol.ManifestFile} is not valid JSON: {ex.Message}"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginManifestException(folder, [$"{PluginProtocol.ManifestFile} cannot be read: {ex.Message}"]);
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

    /// <summary>
    /// The file's bytes as a manifest, unchecked; null for a JSON <c>null</c>. The one way plugin.json is read, by
    /// <see cref="Load"/> and by the trust record alike, so that both always see the same <see cref="Program"/>.
    /// </summary>
    /// <exception cref="JsonException">The bytes are not a manifest in JSON.</exception>
    internal static PluginManifest? Parse(byte[] json)
    {
        // UTF-8 unless a byte order mark says otherwise, which it skips.
        using var reader = new StreamReader(new MemoryStream(json), detectEncodingFromByteOrderMarks: true);
        return JsonSerializer.Deserialize(reader.ReadToEnd(), PluginJsonContext.Default.PluginManifest);
    }

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
            problems.Add($"id '{Show(Id)}' {PluginIds.Rule}");
        }

        // These strings reach the terminal before the user is asked anything, so none may carry an escape sequence or
        // a character that reorders or splits the line; nor may any other text in the file.
        RefuseHidden("id", Id, problems);
        RefuseHidden("name", Name, problems);
        RefuseHidden("version", Version, problems);
        RefuseHidden("publisher", Publisher, problems);
        RefuseHidden("homepage", Homepage, problems);

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("name must not be empty");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            problems.Add("version must not be empty");
        }

        CheckProgram(folder, problems);
        CheckProgramFiles(folder, problems);
        CheckModels(problems);
        return problems;
    }

    /// <summary>
    /// Whether an entry of <see cref="Program"/> may be read as a path inside the plugin's folder: a relative path
    /// without hidden characters. The trust record skips any other, as this check refuses it.
    /// </summary>
    internal static bool IsProgramEntry(string? entry) => entry is not null && !PluginText.HasHidden(entry) && IsInsideFolder(entry);

    private static void RefuseHidden(string field, string? value, List<string> problems)
    {
        if (PluginText.HasHidden(value))
        {
            problems.Add($"{field} must not contain control or invisible formatting characters");
        }
    }

    /// <summary>A manifest value for a message, its hidden characters spelt out, so a message never moves the cursor.</summary>
    private static string Show(string? value) => PluginText.Show(value);

    private void CheckProgram(string folder, List<string> problems)
    {
        // Every system's entry is text in the file, though only this system's program runs here.
        foreach (var (system, program) in Executable ?? new Dictionary<string, string>())
        {
            RefuseHidden($"system {Show(system)} in executable", system, problems);
            RefuseHidden($"program for {Show(system)}", program, problems);
        }

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
            problems.Add($"the program {Show(file)} for {rid} is not in the folder");
        }
    }

    /// <summary>The files and folders the program is made of, each of which must be in the folder now.</summary>
    private void CheckProgramFiles(string folder, List<string> problems)
    {
        for (var i = 0; i < (Program?.Count ?? 0); i++)
        {
            var entry = Program![i];
            if (entry is null)
            {
                problems.Add($"program[{Index(i)}] is empty");
            }
            else if (PluginText.HasHidden(entry))
            {
                RefuseHidden($"program[{Index(i)}]", entry, problems);
            }
            else if (!IsInsideFolder(entry))
            {
                problems.Add($"program lists '{Show(entry)}', which must be a file or folder name inside the plugin's folder");
            }
            else if (!File.Exists(Path.Combine(folder, entry)) && !Directory.Exists(Path.Combine(folder, entry)))
            {
                problems.Add($"program lists '{Show(entry)}', which is not in the plugin's folder");
            }
            else if (IsThroughLink(folder, entry))
            {
                problems.Add($"program lists '{Show(entry)}', which is a link or is inside a linked folder");
            }
        }
    }

    /// <summary>
    /// Whether the entry, or a folder on the way to it, is a link. The trust record does not enter a linked folder, and
    /// one on the way leads out of the plugin's folder, so the author would believe a file is checked when it is not.
    /// </summary>
    private static bool IsThroughLink(string folder, string entry)
    {
        var path = folder;
        foreach (var segment in entry.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            path = Path.Combine(path, segment);
            if (PluginProgramFiles.IsLink(Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInside(string folder, string file)
    {
        if (Path.IsPathRooted(file))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(Path.Combine(folder, file)).StartsWith(root, PathComparison.Current);
        }
        catch (ArgumentException)
        {
            // A character no path may hold, such as a null; a control character problem names it.
            return false;
        }
    }

    private void CheckModels(List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (Models?.Count ?? 0); i++)
        {
            var model = Models![i];
            if (model is null)
            {
                problems.Add($"models[{Index(i)}] is empty");
                continue;
            }

            if (string.IsNullOrWhiteSpace(model.Id))
            {
                problems.Add("a model has no id");
                continue;
            }

            var shown = Show(model.Id);
            if (!PluginIds.IsValidModelId(model.Id))
            {
                problems.Add($"model id '{shown}' {PluginIds.ModelRule}");
            }

            if (!seen.Add(model.Id))
            {
                problems.Add($"model id '{shown}' appears twice");
            }

            RefuseHidden($"model {shown}: id", model.Id, problems);
            RefuseHidden($"model {shown}: title", model.Title, problems);
            CheckLanguages(shown, model.Languages, problems);
            RefuseHidden($"model {shown}: licence name", model.Licence?.Name, problems);
            RefuseHidden($"model {shown}: licence url", model.Licence?.Url, problems);
            if (model.Files is null || model.Files.Count == 0)
            {
                problems.Add($"model {shown} lists no files");
                continue;
            }

            for (var f = 0; f < model.Files.Count; f++)
            {
                if (model.Files[f] is { } file)
                {
                    CheckModelFile(shown, file, problems);
                }
                else
                {
                    problems.Add($"model {shown}: files[{Index(f)}] is empty");
                }
            }
        }
    }

    private static void CheckLanguages(string modelId, IReadOnlyList<string>? languages, List<string> problems)
    {
        if (languages is null || languages.Count == 0)
        {
            problems.Add($"model {modelId} must list its languages");
            return;
        }

        for (var i = 0; i < languages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(languages[i]))
            {
                problems.Add($"model {modelId}: languages[{Index(i)}] is empty");
            }
            else
            {
                RefuseHidden($"model {modelId}: languages[{Index(i)}]", languages[i], problems);
            }
        }
    }

    private static string Index(int i) => i.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Relative, with no empty, <c>.</c> or <c>..</c> segment on either kind of separator, so no leading separator
    /// either; subfolders are fine. A manifest serves every system, so a drive, as in <c>C:\x</c>, is refused on Linux too.
    /// </summary>
    private static bool IsInsideFolder(string? file) =>
        !string.IsNullOrEmpty(file)
        && !Path.IsPathRooted(file)
        && !(file.Length >= 2 && char.IsAsciiLetter(file[0]) && file[1] == ':')
        && file.Split('/', '\\').All(segment => segment is not ("" or "." or ".."));

    /// <param name="modelId">Already shown: its hidden characters spelt out.</param>
    private static void CheckModelFile(string modelId, PluginManifestFile file, List<string> problems)
    {
        var shown = Show(file.File);
        RefuseHidden($"model {modelId}: file name", file.File, problems);
        RefuseHidden($"model {modelId}: url", file.Url, problems);
        if (!IsInsideFolder(file.File))
        {
            problems.Add($"model {modelId}: {shown} must be a file name inside the model's folder");
        }

        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"model {modelId}: {shown} must come from an https address");
        }

        if (file.Sha256 is not { Length: Sha256HexLength } hash || !hash.All(char.IsAsciiHexDigit))
        {
            problems.Add($"model {modelId}: {shown} needs a SHA-256");
        }

        if (file.Size <= 0)
        {
            problems.Add($"model {modelId}: {shown} needs its size in bytes");
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
