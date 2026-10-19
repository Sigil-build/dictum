using Dictum.Core.Configuration;

namespace Dictum.App.Overlay;

public enum OverlayPhase
{
    Hidden,
    Listening,
    Transcribing,
    CleaningUp,
    Done,
    Cancelled,
    Error,
}

/// <summary>Everything one frame of the overlay draws.</summary>
/// <param name="Since">Seconds since the phase began.</param>
/// <param name="Time">Seconds on the overlay's clock; the waves drift and breathe on it.</param>
/// <param name="Appear">0–1: the fade-in after the overlay appears.</param>
/// <param name="BandMeans">One smoothed band mean per wave.</param>
/// <param name="Chip">"UK/EN" while listening, the picked code when done; null unless the profile lists languages.</param>
/// <param name="Message">The error line.</param>
public sealed record OverlayScene(
    OverlayPhase Phase,
    double Since,
    double Time,
    double Appear,
    double Level,
    bool Quiet,
    IReadOnlyList<double> BandMeans,
    string? Chip,
    string? Message,
    bool ReducedMotion,
    OverlayPalette Palette)
{
    public static readonly OverlayScene Hidden = new(OverlayPhase.Hidden, 0, 0, 0, 0, false, [0, 0, 0, 0, 0], null, null, false, OverlayPalette.Aurora);
}
