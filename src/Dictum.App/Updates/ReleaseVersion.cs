using System.Globalization;

namespace Dictum.App.Updates;

/// <summary>
/// A release's <c>X.Y.Z</c>, as Dictum's tags name them (<c>v0.4.0</c>). Only plain versions count: a tag with a
/// pre-release or build suffix, or anything else, is no version, so the update check ignores it.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    private const int Parts = 3;

    /// <summary><c>v1.2.3</c> or <c>1.2.3</c>; null for anything else.</summary>
    public static ReleaseVersion? Parse(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return null;
        }

        var text = tag[0] is 'v' or 'V' ? tag[1..] : tag;
        var parts = text.Split('.');
        if (parts.Length != Parts)
        {
            return null;
        }

        var numbers = new int[Parts];
        for (var i = 0; i < Parts; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>The app's own version from its informational version: VersionPrefix, without a suffix or build metadata.</summary>
    public static ReleaseVersion? OfApp(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        var end = informationalVersion.IndexOfAny(['-', '+']);
        return Parse(end < 0 ? informationalVersion : informationalVersion[..end]);
    }

    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    /// <summary>"0.4.0", as the rail and the tray show a version.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
