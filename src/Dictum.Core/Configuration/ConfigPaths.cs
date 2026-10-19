using System.Text.Json;

namespace Dictum.Core.Configuration;

/// <summary>
/// The paths that <see cref="ConfigIssue.Path"/> carries, such as <c>stt.local.model</c>; Settings matches them to its
/// fields. Fields are property names (<c>nameof</c>), written with the camel-case policy of the config's JSON.
/// </summary>
public static class ConfigPaths
{
    private static readonly string GeneralRoot = JsonName(nameof(DictumConfig.General));
    private static readonly string SttRoot = JsonName(nameof(DictumConfig.Stt));
    private static readonly string ProcessorsRoot = JsonName(nameof(DictumConfig.Processors));
    private static readonly string ProfilesRoot = JsonName(nameof(DictumConfig.Profiles));

    public static string General(string field) => Field(GeneralRoot, field);

    public static string Stt(string id, string? field = null) => WithField($"{SttRoot}.{id}", field);

    public static string Processor(string id, string? field = null) => WithField($"{ProcessorsRoot}.{id}", field);

    public static string Profile(int index, string? field = null) => WithField(Item(ProfilesRoot, index), field);

    /// <summary>A field below <paramref name="path"/>, such as <c>general.overlay</c> → <c>general.overlay.opacity</c>.</summary>
    public static string Field(string path, string field) => $"{path}.{JsonName(field)}";

    /// <summary>One entry of the list at <paramref name="path"/>, such as <c>profiles[0].vocabulary[2]</c>.</summary>
    public static string Item(string path, int index) => $"{path}[{index}]";

    private static string WithField(string path, string? field) => field is null ? path : Field(path, field);

    private static string JsonName(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);
}
