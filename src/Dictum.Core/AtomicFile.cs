using System.Text;

namespace Dictum.Core;

/// <summary>
/// Writes a file through a temporary sibling and a rename, so a crash never leaves it half-written and a reader never
/// sees half of it. The text is UTF-8 without a byte-order mark.
/// </summary>
public static class AtomicFile
{
    private const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void WriteAllText(string path, string content) =>
        Replace(path, temp => File.WriteAllText(temp, content, Utf8NoBom));

    private static void Replace(string path, Action<string> write)
    {
        var temp = path + TempSuffix;
        write(temp);
        File.Move(temp, path, overwrite: true);
    }
}
