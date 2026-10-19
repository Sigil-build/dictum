namespace Dictum.Core;

/// <summary>How file paths compare on this OS: Windows and macOS file systems ignore case by default, Linux's do not.</summary>
public static class PathComparison
{
    public static StringComparison Current { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
