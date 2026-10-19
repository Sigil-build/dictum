using System.Text.Json;
using System.Text.Json.Serialization;
using Dictum.Core.Configuration;

namespace Dictum.Setup;

public enum CatalogKind
{
    [JsonStringEnumMemberName("whisper")]
    Whisper,

    [JsonStringEnumMemberName("vad")]
    Vad,
}

/// <summary>The hardware a speech model is meant for; <c>ci</c> entries exist for tests only.</summary>
public enum CatalogTier
{
    [JsonStringEnumMemberName("gpu")]
    Gpu,

    [JsonStringEnumMemberName("cpu")]
    Cpu,

    [JsonStringEnumMemberName("ci")]
    Ci,
}

/// <param name="Notice">Terms that must be shown before the download, or null.</param>
public sealed record CatalogLicence(string Name, string Url, string? Notice = null);

/// <param name="File">File name in the models directory; the config refers to models by this name.</param>
/// <param name="Url">Download URL pinned to a commit, so the file behind it never changes.</param>
/// <param name="Languages">ISO 639-1 codes the model understands, or <see cref="AnyLanguage"/> for a multilingual model.</param>
/// <param name="RelativeCost">Encoder work relative to Whisper tiny (1), for speed estimates; null for non-speech files.</param>
/// <param name="Hidden">Not offered by <c>dictum init</c> or listed by default; still downloadable by id.</param>
public sealed record CatalogEntry(
    string Id,
    CatalogKind Kind,
    string Title,
    string File,
    string Url,
    string Sha256,
    long Size,
    CatalogLicence Licence,
    IReadOnlyList<string> Languages,
    CatalogTier? Tier = null,
    bool Hidden = false,
    double? RelativeCost = null)
{
    /// <summary>The <see cref="Languages"/> entry of a multilingual model.</summary>
    public const string AnyLanguage = "*";

    public bool Understands(string? language) =>
        Languages.Contains(AnyLanguage) || (language is not null && Languages.Contains(language));
}

/// <summary>Models Dictum can download, with pinned URLs, sizes and SHA-256 hashes. Built in, so it works offline.</summary>
public sealed class Catalog
{
    /// <summary>The LogicalName that Dictum.Setup.csproj gives catalog.json.</summary>
    private const string ResourceName = "Dictum.Setup.catalog.json";

    private Catalog(IReadOnlyList<CatalogEntry> entries) => Entries = entries;

    public IReadOnlyList<CatalogEntry> Entries { get; }

    public static Catalog BuiltIn { get; } = Parse(ReadEmbedded());

    public static Catalog Parse(string json) =>
        new((JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogDocument)
            ?? throw new InvalidDataException("The model catalog is empty.")).Entries);

    public CatalogEntry? Find(string id) =>
        Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public CatalogEntry Get(string id) =>
        Find(id) ?? throw new ConfigException(
            $"Unknown model '{id}'. Known: {string.Join(", ", Entries.Where(e => !e.Hidden).Select(e => e.Id))}.");

    public CatalogEntry? ForFile(string file) =>
        Entries.FirstOrDefault(e => string.Equals(e.File, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));

    private static string ReadEmbedded()
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The model catalog is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed record CatalogDocument(int Version, IReadOnlyList<CatalogEntry> Entries);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(CatalogDocument))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
