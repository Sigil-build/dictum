namespace Dictum.App.Shell;

/// <summary>Dictum's pages on GitHub, which Settings → About Dictum opens.</summary>
public static class ProjectLinks
{
    public static Uri Repository { get; } = new("https://github.com/Sigil-build/dictum");

    public static Uri Releases { get; } = new("https://github.com/Sigil-build/dictum/releases");

    /// <summary>The issue form chooser: a bug report, or a private report for a security issue.</summary>
    public static Uri NewIssue { get; } = new("https://github.com/Sigil-build/dictum/issues/new/choose");

    /// <summary>The license, as the documentation site links it.</summary>
    public static Uri License { get; } = new("https://github.com/Sigil-build/dictum/blob/HEAD/LICENSE");

    /// <summary>The notices of the newest code, for a build that has no copy next to it.</summary>
    public static Uri Notices { get; } = new("https://github.com/Sigil-build/dictum/blob/HEAD/THIRD-PARTY-NOTICES.txt");
}
