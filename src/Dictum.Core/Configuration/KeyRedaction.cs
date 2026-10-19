using System.Text;

namespace Dictum.Core.Configuration;

/// <summary>
/// Prints the records that can hold an API key without the key. A record prints its members wherever it is formatted:
/// a log line, an exception message, a failed assertion.
/// </summary>
public static class KeyRedaction
{
    public const string Mask = "***";

    /// <summary>A reference (<c>env:VAR</c>, <c>secret:name</c>) as it is; a key given as a value as <see cref="Mask"/>.</summary>
    public static string? Redact(string? value) => value is null || ConfigValueResolver.IsReference(value) ? value : Mask;

    /// <summary>The compiler's record format, with the members of a copy whose key is already redacted.</summary>
    public static string Print(string typeName, Func<StringBuilder, bool> printMembers)
    {
        var text = new StringBuilder(typeName).Append(" { ");
        if (printMembers(text))
        {
            text.Append(' ');
        }

        return text.Append('}').ToString();
    }
}
