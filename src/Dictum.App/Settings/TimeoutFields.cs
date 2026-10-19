namespace Dictum.App.Settings;

/// <summary>Timeouts are milliseconds in the config and seconds in the Settings fields.</summary>
internal static class TimeoutFields
{
    public static decimal Seconds(int milliseconds) => milliseconds / (decimal)TimeSpan.MillisecondsPerSecond;

    public static int Milliseconds(decimal seconds) => (int)decimal.Truncate(seconds * TimeSpan.MillisecondsPerSecond);
}
