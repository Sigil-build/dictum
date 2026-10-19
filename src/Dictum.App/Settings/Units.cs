using System.Globalization;
using Dictum.App.Resources;
using Dictum.Core;

namespace Dictum.App.Settings;

/// <summary>
/// Sizes, rates and times for the UI, in the UI culture's digits and units. File sizes are decimal, as model sites show
/// them ("874 MB", "1.53 GB"); memory is binary, as Task Manager labels it ("15.8 GB").
/// </summary>
public static class Units
{
    private const double BytesPerMegabyte = ByteUnits.Megabyte;
    private const double BytesPerGigabyte = ByteUnits.Gigabyte;
    private const double BytesPerGibibyte = ByteUnits.Gibibyte;

    /// <summary>From ten units up, a decimal adds nothing: "16 MB", "16 s", but "1.5 MB", "0.6 s".</summary>
    private const double WholeFrom = 10;

    private const string Whole = "0";
    private const string UpToOneDecimal = "0.#";
    private const string UpToTwoDecimals = "0.##";
    private const string OneDecimal = "0.0";

    public static string Size(long bytes) => bytes >= BytesPerGigabyte
        ? Format(Strings.UnitGigabytes, bytes / BytesPerGigabyte, UpToTwoDecimals)
        : Format(Strings.UnitMegabytes, bytes / BytesPerMegabyte, bytes >= WholeFrom * BytesPerMegabyte ? Whole : UpToOneDecimal);

    public static string Memory(long bytes) => Format(Strings.UnitGigabytes, bytes / BytesPerGibibyte, OneDecimal);

    public static string Rate(double bytesPerSecond) => Format(Strings.UnitMegabytesPerSecond, bytesPerSecond / BytesPerMegabyte,
        bytesPerSecond >= WholeFrom * BytesPerMegabyte ? Whole : UpToOneDecimal);

    /// <summary>"0.6 s", "6 s", "16 s".</summary>
    public static string Seconds(double seconds) => Format(Strings.UnitSeconds, seconds, seconds >= WholeFrom ? Whole : UpToOneDecimal);

    /// <summary>"32 s left", "5 min left", "1 h 5 min left"; rounded up, so it never reads zero.</summary>
    public static string TimeLeft(TimeSpan left)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(left.TotalSeconds));
        if (seconds < TimeSpan.SecondsPerMinute)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsSecondsLeft, seconds);
        }

        var minutes = (seconds + TimeSpan.SecondsPerMinute - 1) / TimeSpan.SecondsPerMinute;
        return minutes < TimeSpan.MinutesPerHour
            ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsMinutesLeft, minutes)
            : string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsHoursLeft, minutes / TimeSpan.MinutesPerHour, minutes % TimeSpan.MinutesPerHour);
    }

    private static string Format(string unit, double value, string digits) =>
        string.Format(CultureInfo.CurrentCulture, unit, value.ToString(digits, CultureInfo.CurrentCulture));
}
