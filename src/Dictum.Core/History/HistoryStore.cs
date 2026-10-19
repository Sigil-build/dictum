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
    string? Error = null,
    string? Model = null,
    string? Backend = null,
    long? TranscribeMs = null);

/// <summary>Keeps the last N transcriptions as JSON lines. Audio is never stored.</summary>
public sealed class HistoryStore(string path, int maxEntries)
{
    /// <summary>The same on every OS, for appends and rewrites alike; reading also accepts <c>\r\n</c>.</summary>
    private const string LineEnd = "\n";

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
            File.AppendAllText(Path, JsonSerializer.Serialize(entry, HistoryJsonContext.Default.HistoryEntry) + LineEnd, Utf8);

            var lines = File.ReadAllLines(Path, Utf8);
            if (lines.Length > maxEntries)
            {
                Rewrite(lines[^maxEntries..]);
            }
        }
    }

    /// <summary>Removes the entry recorded at <paramref name="at"/>; false when there is none.</summary>
    /// <remarks>A delete racing an append from another process can lose that one append; deletes are rare and manual.</remarks>
    public bool Delete(DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                return false;
            }

            var lines = File.ReadAllLines(Path, Utf8);
            var kept = lines.Where(line => !RecordedAt(line, at)).ToList();
            if (kept.Count == lines.Length)
            {
                return false;
            }

            Rewrite(kept);
            return true;
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
                    // A line cut short by a crash or edited by hand is skipped; the rest of the history still loads.
                }
            }

            return entries.Count > count ? entries[^count..] : entries;
        }
    }

    private void Rewrite(IEnumerable<string> lines) =>
        AtomicFile.WriteAllText(Path, string.Concat(lines.Select(line => line + LineEnd)));

    private static bool RecordedAt(string line, DateTimeOffset at)
    {
        try
        {
            return JsonSerializer.Deserialize(line, HistoryJsonContext.Default.HistoryEntry)?.At == at;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HistoryEntry))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
