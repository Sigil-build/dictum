using Dictum.Core.Configuration;

namespace Dictum.App.Overlay;

/// <summary>One of the five waves: cycles across the width, phase, drift (rad/s), weight, its bands (inclusive) and core alpha.</summary>
public readonly record struct WaveSpec(double Frequency, double Phase, double Drift, double Weight, int FirstBand, int LastBand, double Alpha);

/// <summary>A palette: three wave stops and two cleaning-up stops (ARGB), and the glow opacity.</summary>
public sealed record PaletteSpec(uint Wave1, uint Wave2, uint Wave3, uint Cleaning1, uint Cleaning2, double GlowOpacity);

/// <summary>
/// The overlay's design tokens, in overlay units (<see cref="Width"/> × <see cref="Height"/> at medium size and 100 %
/// scaling). Durations are in seconds, colours are ARGB.
/// </summary>
public static class OverlayTokens
{
    public const double Width = 480;
    public const double Height = 96;
    public const double CenterY = 44;
    public const double MarginFromPanel = 4;

    public const double FloorDb = -60;
    public const double CeilingDb = -26;
    public const double Attack = 0.030;
    public const double Release = 0.160;
    public const double MaxStep = 0.05;
    public const double SilenceThreshold = 0.04;
    public const double SilenceHold = 0.5;
    public const int BandCount = 24;

    public const double EnvelopeSigma = 0.17;
    public const double AmplitudeFromLevel = 26;
    public const double AmplitudeFromBands = 26;
    public const double BreatheBase = 1.2;
    public const double ListeningBreatheSwing = 1.0;
    public const double QuietBreatheSwing = 1.2;
    public const double BreatheHz = 0.25;
    public const double QuietAlpha = 0.35;
    public const double Step = 4;
    public const double GradientInset = 40;

    public const double CoreWidth = 1.6;
    public const double GlowWidth = 9;
    public const double ShadowWidth = 3;
    public const double ShadowAlpha = 0.25;
    public const double ShadowOffsetY = 1;

    public const double TranscribingAmplitude = 2;
    public const double TranscribingCycles = 1.3;
    public const double TranscribingSpeed = 2;
    public const double BumpHeight = 10;
    public const double BumpSigma = 0.045;
    public const double BumpPeriod = 1.3;
    public const double BumpStart = -0.15;
    public const double CleaningAmplitude = 7;
    public const double CleaningHz = 1.4;

    public const double Appear = 0.120;
    public const double TranscribingMerge = 0.320;
    public const double CleaningEnter = 0.250;
    public const double DoneContract = 0.160;
    public const double DoneCheckDelay = 0.060;
    public const double DoneCheck = 0.140;
    public const double DoneFadeDelay = 0.180;
    public const double DoneFade = 0.120;
    public const double Done = 0.300;
    public const double DoneInset = 20;
    public const double CheckWidth = 2.4;
    public const double CheckShadowAlpha = 0.6;
    public const double Cancelled = 0.240;
    public const double CancelledSlide = 70;
    public const double CancelledGap = 5;
    public const double ErrorFlashPeriod = 0.180;
    public const int ErrorFlashCount = 2;
    public const double ErrorFlashLow = 0.35;
    public const double ErrorTextDelay = 0.100;
    public const double ErrorHold = 2.0;
    public const double ErrorFade = 0.300;
    public const double IconStroke = 1.8;
    public const double IconShadowAlpha = 0.7;
    public const double IdleGrace = 1.0;

    public const double MeterWidth = 240;
    public const double MeterHeight = 6;
    public const double MeterRadius = 3;
    public const double MeterMinFill = 6;
    public const double MeterAlpha = 0.95;
    public const double MeterStaticAlpha = 0.6;
    public const double MeterQuietFill = 0.02;
    public const double MeterTranscribingFill = 0.5;
    public const double MeterCleaningFill = 0.85;
    public const double MeterShadowAlpha = 0.25;

    public const double CaptionSize = 11;
    public const double ErrorTextSize = 12;
    public const double ChipSize = 10;
    public const double CaptionOffset = 24;
    public const double ReducedCaptionOffset = 22;
    public const double CaptionAlpha = 0.85;
    public const double ChipHeight = 16;
    public const double ChipRadius = 4;
    public const double ChipPadding = 6;
    public const double ChipAlpha = 0.9;
    public const double ChipRight = 16;
    public const double IconOffsetX = -72;
    public const double ErrorTextOffsetX = -58;

    public const uint TextFill = 0xFFF4F6FB;
    public const uint TextHalo = 0xBF000000;
    public const uint ChipBackground = 0x99141622;
    public const uint DoneStroke = 0xFFDCE6FF;
    public const uint CancelledStroke = 0xFF9AA0AE;
    public const uint ErrorStroke = 0xFFFF5A4E;
    public const uint ErrorIcon = 0xFFFF8A80;
    public const uint Shadow = 0xFF080A18;
    public const uint Track = 0x598088A0;

    public static readonly IReadOnlyList<WaveSpec> Waves =
    [
        new(1.0, 0.0, 0.9, 1.00, 0, 5, 0.95),
        new(1.5, 1.2, -0.7, 0.80, 4, 9, 0.85),
        new(2.0, 2.5, 1.1, 0.65, 8, 14, 0.75),
        new(2.6, 3.9, -1.3, 0.50, 13, 19, 0.65),
        new(3.3, 5.1, 1.6, 0.38, 18, 23, 0.55),
    ];

    public static double SizeScale(OverlaySize size) => size switch
    {
        OverlaySize.Small => 0.8,
        OverlaySize.Large => 1.25,
        _ => 1.0,
    };

    public static PaletteSpec Palette(OverlayPalette palette) => palette switch
    {
        OverlayPalette.Ember => new(0xFFFF8A3D, 0xFFFF5E8A, 0xFFC84BFF, 0xFFFF5E8A, 0xFFC84BFF, 0.22),
        OverlayPalette.Mist => new(0xFFCDD3E0, 0xFFCDD3E0, 0xFFCDD3E0, 0xFFCDD3E0, 0xFFCDD3E0, 0.10),
        _ => new(0xFF3D8BFF, 0xFF7A6BFF, 0xFFB266FF, 0xFF7A6BFF, 0xFFC96BFF, 0.22),
    };
}
