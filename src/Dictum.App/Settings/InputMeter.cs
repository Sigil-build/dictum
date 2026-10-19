using System.Globalization;
using Dictum.App.Overlay;
using Dictum.App.Resources;

namespace Dictum.App.Settings;

/// <summary>
/// The microphone meter's maths: rms to a 0–1 level between −60 and −10 dBFS, smoothed like the overlay (attack 30 ms,
/// release 160 ms, steps capped at 50 ms). Ordinary speech at −31 dBFS lights 17 of 30 segments; the overlay's own
/// range ends at −26 dBFS, which would light red on ordinary speech.
/// </summary>
public sealed class InputMeter
{
    public const int Segments = 30;
    public const double FloorDb = -60;
    public const double CeilingDb = -10;

    /// <summary>Amplitude to decibels: 20 · log10.</summary>
    private const double DbPerDecade = 20;

    /// <summary>The rms that counts as silence, −120 dBFS, so the logarithm stays finite.</summary>
    private const double SilenceRms = 1e-6;

    private const string TypographicMinus = "−";

    /// <summary>A frame older than this counts as silence: the monitor stopped, or frames were dropped.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMilliseconds(250);

    private TimeSpan? _last;

    public double Level { get; private set; }

    /// <summary>The dBFS the smoothed level stands for, rounded.</summary>
    public int Db => (int)Math.Round(FloorDb + Level * (CeilingDb - FloorDb), MidpointRounding.AwayFromZero);

    public static double Target(float rms) =>
        Math.Clamp((DbPerDecade * Math.Log10(Math.Max(rms, SilenceRms)) - FloorDb) / (CeilingDb - FloorDb), 0, 1);

    public static int LitFor(double level) => (int)Math.Round(Math.Clamp(level, 0, 1) * Segments, MidpointRounding.AwayFromZero);

    /// <summary>"−31 dBFS", with a typographic minus.</summary>
    public static string Readout(int db)
    {
        var number = Math.Abs(db).ToString(CultureInfo.CurrentCulture);
        return UiText.Format(Strings.SettingsDbfs, db < 0 ? TypographicMinus + number : number);
    }

    public void Update(float rms, TimeSpan now)
    {
        var dt = _last is { } last ? Math.Clamp((now - last).TotalSeconds, 0, OverlayTokens.MaxStep) : OverlayTokens.MaxStep;
        _last = now;
        var target = Target(rms);
        var tau = target > Level ? OverlayTokens.Attack : OverlayTokens.Release;
        Level += (target - Level) * (1 - Math.Exp(-dt / tau));
    }

    public void Reset()
    {
        Level = 0;
        _last = null;
    }
}
