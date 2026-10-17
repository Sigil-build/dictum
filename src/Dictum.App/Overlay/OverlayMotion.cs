namespace Dictum.App.Overlay;

/// <summary>The end states' timing: durations and easings from the motion tokens, shapes from the prototype.</summary>
public static class OverlayMotion
{
    /// <summary>Half the Done line: 220 units contracting to 0 over 160 ms on the accelerate curve.</summary>
    public static double DoneHalfWidth(double since) =>
        (OverlayTokens.Width / 2 - OverlayTokens.DoneInset) * (1 - CubicBezier.Accelerate.Ease(since / OverlayTokens.DoneContract));

    /// <summary>How much of the check is drawn: from 60 ms to 200 ms.</summary>
    public static double DoneCheck(double since) =>
        Math.Clamp((since - OverlayTokens.DoneCheckDelay) / OverlayTokens.DoneCheck, 0, 1);

    /// <summary>Done's alpha: 1 until 180 ms, then linear to 0 at 300 ms.</summary>
    public static double DoneAlpha(double since) =>
        since < OverlayTokens.DoneFadeDelay ? 1 : Math.Max(0, 1 - (since - OverlayTokens.DoneFadeDelay) / OverlayTokens.DoneFade);

    /// <summary>Cancelled over 240 ms on the accelerate curve: the halves slide out 70 units as the alpha falls to 0.</summary>
    public static double CancelledProgress(double since) => CubicBezier.Accelerate.Ease(since / OverlayTokens.Cancelled);

    /// <summary>Cancelled in reduced motion: a linear fade over 240 ms.</summary>
    public static double CancelledFade(double since) => Math.Max(0, 1 - since / OverlayTokens.Cancelled);

    /// <summary>The error line blinks dim twice in its first 360 ms (1 / 0.35, 90 ms each).</summary>
    public static double ErrorFlash(double since) =>
        since >= 0 && since < OverlayTokens.ErrorFlashPeriod * OverlayTokens.ErrorFlashCount
            && Math.Sin(since * 2 * Math.PI / OverlayTokens.ErrorFlashPeriod) <= 0
            ? OverlayTokens.ErrorFlashLow
            : 1;

    /// <summary>The error's alpha: held for 2 s, then a linear 300 ms fade.</summary>
    public static double ErrorAlpha(double since) =>
        since > OverlayTokens.ErrorHold ? Math.Max(0, 1 - (since - OverlayTokens.ErrorHold) / OverlayTokens.ErrorFade) : 1;
}
