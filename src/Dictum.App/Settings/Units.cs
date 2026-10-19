using System.Globalization;
using Dictum.App.Resources;

namespace Dictum.App.Settings;

/// <summary>
/// Sizes, rates and times for the UI, in the UI culture's digits and units. File sizes are decimal, as model sites show
/// them ("874 MB", "1.53 GB"); memory is binary, as Task Manager labels it ("15.8 GB").
/// </summary>
public static class Units
{
    public static string Size(long bytes) => bytes >= 1_000_000_000
        ? Format(Strings.UnitGigabytes, bytes / 1e9, "0.##")
        : Format(Strings.UnitMegabytes, bytes / 1e6, bytes >= 10_000_000 ? "0" : "0.#");

    public static string Memory(long bytes) => Format(Strings.UnitGigabytes, bytes / 1073741824.0, "0.0");

    public static string Rate(double bytesPerSecond) =>
        Format(Strings.UnitMegabytesPerSecond, bytesPerSecond / 1e6, bytesPerSecond >= 10_000_000 ? "0" : "0.#");

    /// <summary>"0.6 s", "6 s", "16 s".</summary>
    public static string Seconds(double seconds) => Format(Strings.UnitSeconds, seconds, seconds >= 10 ? "0" : "0.#");

    /// <summary>"32 s left", "5 min left", "1 h 5 min left"; rounded up, so it never reads zero.</summary>
    public static string TimeLeft(TimeSpan left)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(left.TotalSeconds));
        if (seconds < 60)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsSecondsLeft, seconds);
        }

        var minutes = (seconds + 59) / 60;
        return minutes < 60
            ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsMinutesLeft, minutes)
            : string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsHoursLeft, minutes / 60, minutes % 60);
    }

    private static string Format(string unit, double value, string digits) =>
        string.Format(CultureInfo.CurrentCulture, unit, value.ToString(digits, CultureInfo.CurrentCulture));
}
