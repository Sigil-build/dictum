using Dictum.Core;
using Dictum.Core.Configuration;

namespace Dictum.Processing;

/// <summary>
/// The prompts a user can pick and edit. Built-ins ship with Dictum; <c>&lt;name&gt;.md</c> in the prompts folder adds a
/// prompt or replaces a built-in of the same name, which is how <see cref="PromptTemplates"/> loads them.
/// </summary>
public sealed class PromptLibrary(string directory)
{
    public IReadOnlyList<string> Names()
    {
        var names = new SortedSet<string>(PromptTemplates.BuiltIn, StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(directory))
            {
                names.UnionWith(Directory.EnumerateFiles(directory, "*" + PromptTemplates.Extension).Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(IsValidName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unlistable folder still lists the built-ins; opening a prompt there shows the error.
        }

        return [.. names];
    }

    public static bool IsBuiltIn(string name) => PromptTemplates.BuiltIn.Contains(name);

    public bool HasFile(string name) => IsValidName(name) && File.Exists(PathOf(name));

    /// <summary>The template the daemon would use; empty when there is none yet. A file that cannot be read throws.</summary>
    public string Read(string name)
    {
        try
        {
            return new PromptTemplates(directory).Load(name);
        }
        catch (ConfigException)
        {
            return "";
        }
    }

    /// <summary>Writes through a temporary file, so the daemon never reads half a prompt.</summary>
    public void Write(string name, string text)
    {
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(PathOf(name), text);
    }

    public void Delete(string name)
    {
        if (HasFile(name))
        {
            File.Delete(PathOf(name));
        }
    }

    /// <summary>A name the prompts folder can hold as one file: not empty, not padded, no folder or parent part.</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name == name.Trim() && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(['/', '\\']) < 0 && !name.Contains("..", StringComparison.Ordinal);

    private string PathOf(string name) => PromptTemplates.FileIn(directory, name);
}
