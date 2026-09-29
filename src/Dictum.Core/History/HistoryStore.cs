using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictum.Core.History;

/// <param name="RawText">Text straight from STT, before processing.</param>
/// <param name="Error">Set when processing failed; <see cref="Text"/> then equals the raw text.</param>
public sealed record HistoryEntry(
    DateTimeOffset At,
    string ProfileId,
    string Provider,
    string RawText,
    string Text,
    string? Language,
    double AudioSeconds,
    string? Error = null);

/// <summary>Keeps the last N transcriptions as JSON lines. Audio is never stored.</summary>
public sealed class HistoryStore(string path, int maxEntries)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public void Append(HistoryEntry entry)
    {
        if (maxEntries <= 0)
        {
            return;
        }

        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, JsonSerializer.Serialize(entry, HistoryJsonContext.Default.HistoryEntry) + "\n", Utf8);

            var lines = File.ReadAllLines(Path, Utf8);
            if (lines.Length > maxEntries)
            {
                var temp = Path + ".tmp";
                File.WriteAllLines(temp, lines[^maxEntries..], Utf8);
                File.Move(temp, Path, overwrite: true);
            }
        }
    }

    /// <summary>Returns up to <paramref name="count"/> entries, oldest first. Corrupt lines are skipped.</summary>
    public IReadOnlyList<HistoryEntry> ReadLast(int count)
    {
        lock (_gate)
        {
            if (!File.Exists(Path) || count <= 0)
            {
                return [];
            }

            var entries = new List<HistoryEntry>();
            foreach (var line in File.ReadAllLines(Path, Utf8))
            {
                try
                {
                    if (JsonSerializer.Deserialize(line, HistoryJsonContext.Default.HistoryEntry) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return entries.Count > count ? entries[^count..] : entries;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HistoryEntry))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
