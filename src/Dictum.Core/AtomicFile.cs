using System.Text;

namespace Dictum.Core;

/// <summary>Writes a file through a temporary sibling and a rename, so a crash never leaves it half-written.</summary>
internal static class AtomicFile
{
    private const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void WriteAllText(string path, string content) =>
        Replace(path, temp => File.WriteAllText(temp, content, Utf8NoBom));

    public static void WriteAllLines(string path, IEnumerable<string> lines) =>
        Replace(path, temp => File.WriteAllLines(temp, lines, Utf8NoBom));

    private static void Replace(string path, Action<string> write)
    {
        var temp = path + TempSuffix;
        write(temp);
        File.Move(temp, path, overwrite: true);
    }
}
