namespace Dictum.App.Overlay;

/// <summary>The end states' timing: durations and easings from the motion tokens. <c>since</c> is in seconds.</summary>
public static class OverlayMotion
{
    /// <summary>Half the Done line: from the full line less its insets, contracting to 0 on the accelerate curve.</summary>
    public static double DoneHalfWidth(double since) =>
        (OverlayTokens.Width / 2 - OverlayTokens.DoneInset) * (1 - CubicBezier.Accelerate.Ease(since / OverlayTokens.DoneContract));

    /// <summary>How much of the check is drawn, 0–1, after a short delay.</summary>
    public static double DoneCheck(double since) =>
        Math.Clamp((since - OverlayTokens.DoneCheckDelay) / OverlayTokens.DoneCheck, 0, 1);

    /// <summary>Done's alpha: held at 1, then a linear fade to 0.</summary>
    public static double DoneAlpha(double since) =>
        since < OverlayTokens.DoneFadeDelay ? 1 : Math.Max(0, 1 - (since - OverlayTokens.DoneFadeDelay) / OverlayTokens.DoneFade);

    /// <summary>Cancelled on the accelerate curve: the halves slide out as the alpha falls to 0.</summary>
    public static double CancelledProgress(double since) => CubicBezier.Accelerate.Ease(since / OverlayTokens.Cancelled);

    /// <summary>Cancelled in reduced motion: a linear fade.</summary>
    public static double CancelledFade(double since) => Math.Max(0, 1 - since / OverlayTokens.Cancelled);

    /// <summary>
    /// The error line blinks: it dims to <see cref="OverlayTokens.ErrorFlashLow"/> for the second half of each of its first
    /// <see cref="OverlayTokens.ErrorFlashCount"/> flash periods.
    /// </summary>
    public static double ErrorFlash(double since) =>
        since >= 0 && since < OverlayTokens.ErrorFlashPeriod * OverlayTokens.ErrorFlashCount
            && Math.Sin(since * 2 * Math.PI / OverlayTokens.ErrorFlashPeriod) <= 0
            ? OverlayTokens.ErrorFlashLow
            : 1;

    /// <summary>The error's alpha: held, then a linear fade.</summary>
    public static double ErrorAlpha(double since) =>
        since > OverlayTokens.ErrorHold ? Math.Max(0, 1 - (since - OverlayTokens.ErrorHold) / OverlayTokens.ErrorFade) : 1;
}
