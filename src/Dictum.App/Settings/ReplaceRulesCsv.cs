using System.Globalization;
using System.Text;
using Dictum.App.Resources;
using Dictum.Core.Configuration;

namespace Dictum.App.Settings;

/// <summary>
/// Replace rules from a CSV file (spec 7, plan ruling).
/// <list type="bullet">
/// <item>Columns: Find, Replace, then optional Match case and Whole word.</item>
/// <item>A header row (its first cell is "Find" or "Знайти") is skipped, and so are blank lines.</item>
/// <item>Cells are separated by commas or semicolons, whichever the first line uses more; Excel writes semicolons in
/// Ukrainian locales. Quotes work as in RFC 4180, within one line.</item>
/// <item>Bad lines are named and skipped.</item>
/// <item>The file is UTF-8 or UTF-16 with a byte order mark, UTF-8 without one, or else the ANSI code page that Excel's
/// plain "CSV (comma delimited)" uses (final review I4).</item>
/// </list>
/// </summary>
public static class ReplaceRulesCsv
{
    private static readonly string[] Yes = ["true", "yes", "1", "x", "так", "+"];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The file's text, with <see cref="AnsiEncoding"/> as the last resort.</summary>
    public static string Decode(byte[] bytes) => Decode(bytes, AnsiEncoding());

    /// <summary>The file's text: a byte order mark decides; then UTF-8 if the bytes are valid UTF-8; then <paramref name="ansi"/>.</summary>
    public static string Decode(byte[] bytes, Encoding ansi)
    {
        ReadOnlySpan<byte> data = bytes;
        foreach (var encoding in (ReadOnlySpan<Encoding>)[Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode])
        {
            if (data.StartsWith(encoding.Preamble))
            {
                return encoding.GetString(data[encoding.Preamble.Length..]);
            }
        }

        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return ansi.GetString(data);
        }
    }

    /// <summary>
    /// Windows' active code page (1251 on a Ukrainian system); elsewhere, which has no such setting, the current
    /// culture's (1251 for uk, 1252 for en).
    /// </summary>
    public static Encoding AnsiEncoding()
    {
        var codePage = OperatingSystem.IsWindows() ? 0 : CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.Latin1;
    }

    public static (IReadOnlyList<ReplaceRule> Rules, IReadOnlyList<string> Problems) Parse(string csv)
    {
        var lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var separator = first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
        var rules = new List<ReplaceRule>();
        var problems = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (Cells(line, separator) is not { } cells)
            {
                problems.Add(string.Format(CultureInfo.CurrentCulture, Strings.SettingsRulesCsvQuote, i + 1));
                continue;
            }

            if (rules.Count == 0 && problems.Count == 0 && cells[0].Trim() is var head
                && (head.Equals("find", StringComparison.OrdinalIgnoreCase) || head.Equals("знайти", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (cells[0].Length == 0)
            {
                problems.Add(string.Format(CultureInfo.CurrentCulture, Strings.SettingsRulesCsvEmptyFind, i + 1));
                continue;
            }

            rules.Add(new ReplaceRule
            {
                From = cells[0],
                To = cells.Count > 1 ? cells[1] : "",
                IgnoreCase = !(cells.Count > 2 && IsYes(cells[2])),
                WholeWord = cells.Count > 3 && IsYes(cells[3]),
            });
        }

        return (rules, problems);
    }

    private static bool IsYes(string cell) => Yes.Contains(cell.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The cells of one line; null when a quote is not closed.</summary>
    private static List<string>? Cells(string line, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.ToString().Trim().Length == 0)
            {
                cell.Clear();
                quoted = true;
            }
            else if (c == separator)
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        if (quoted)
        {
            return null;
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }
}
