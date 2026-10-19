using Dictum.Core.Configuration;
using Dictum.Processing;

namespace Dictum.App.Settings;

/// <summary>
/// LLM prompt templates as the Processors page edits them. Built-ins ship with Dictum; <c>prompts/&lt;name&gt;.md</c>
/// in the config folder adds a prompt or replaces a built-in of the same name, which is how <see cref="PromptTemplates"/>
/// loads them.
/// </summary>
public sealed class PromptFiles(string directory)
{
    public IReadOnlyList<string> Names()
    {
        var names = new SortedSet<string>(PromptTemplates.BuiltIn, StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(directory))
            {
                names.UnionWith(Directory.EnumerateFiles(directory, "*.md").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(IsValidName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unlistable folder still lists the built-ins; opening a prompt there shows the error.
        }

        return [.. names];
    }

    public bool IsBuiltIn(string name) => PromptTemplates.BuiltIn.Contains(name);

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
        var path = PathOf(name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string name)
    {
        if (HasFile(name))
        {
            File.Delete(PathOf(name));
        }
    }

    public static bool IsValidName(string name) =>
        name.Length > 0 && name == name.Trim() && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(['/', '\\']) < 0 && !name.Contains("..", StringComparison.Ordinal);

    private string PathOf(string name) => Path.Combine(directory, name + ".md");
}
