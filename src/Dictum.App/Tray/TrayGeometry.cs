namespace Dictum.App.Tray;

/// <summary>
/// The tray glyphs on the design's 32-unit grid (tokens <c>tray.*</c>, <c>reference/Tray.dc.html</c>). The ring is
/// Dictum; what sits inside it is the state, so every state reads in monochrome at 16 px.
/// </summary>
public static class TrayGeometry
{
    public const double Grid = 32;
    public const double Centre = 16;
    public const double RingRadius = 13;
    public const double Stroke = 3;

    /// <summary>One wave cycle, stroke 3, round caps.</summary>
    public const string WavePath = "M7 16 C10 16 10.5 9 13 9 C15.5 9 16.5 23 19 23 C21.5 23 22 16 25 16";

    public const double DotRadius = 6;

    public const double DashOn = 7;
    public const double DashOff = 4.6;
    public const int ProcessingFrames = 5;

    public const double ExclamationTop = 9;
    public const double ExclamationBottom = 17.5;
    public const double ExclamationStroke = 3.2;
    public const double ExclamationDotY = 22.5;
    public const double ExclamationDotRadius = 1.9;

    public const double PauseBarWidth = 3.4;
    public const double PauseBarHeight = 12;
    public const double PauseBarTop = 10;
    public const double PauseBarRadius = 1.2;
    public const double PauseBarLeft = 11;
    public const double PauseBarRight = 17.6;

    /// <summary>Recording and error red (<c>color.tray.recording</c>); a layer on top of the shapes, never the only cue.</summary>
    public const string ColourLayer = "#FF5A4E";

    /// <summary><c>color.tray.mono.light</c> and <c>.dark</c>: the shapes on light and dark panels.</summary>
    public const string MonoOnLight = "#1B1B1F";
    public const string MonoOnDark = "#F3F3F5";

    public static readonly int[] Sizes = [16, 20, 24, 32];

    /// <summary>
    /// The dash pattern moves by a fifth of its period per frame, so five frames make one dash step and the
    /// animation loops without a jump.
    /// </summary>
    public static double DashOffset(int frame) => frame % ProcessingFrames * (DashOn + DashOff) / ProcessingFrames;
}
