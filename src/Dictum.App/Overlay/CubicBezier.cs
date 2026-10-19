namespace Dictum.App.Overlay;

/// <summary>A CSS cubic-bezier easing from (0, 0) to (1, 1), the form the motion tokens give their curves in.</summary>
public sealed class CubicBezier(double x1, double y1, double x2, double y2)
{
    /// <summary><c>motion.easing.decelerate</c>.</summary>
    public static readonly CubicBezier Decelerate = new(0, 0, 0, 1);

    /// <summary><c>motion.easing.accelerate</c>.</summary>
    public static readonly CubicBezier Accelerate = new(0.4, 0, 1, 1);

    /// <summary>The eased value at <paramref name="progress"/>, which is clamped to 0–1.</summary>
    public double Ease(double progress)
    {
        var x = Math.Clamp(progress, 0, 1);
        if (x <= 0 || x >= 1)
        {
            return x;
        }

        // x(s) rises monotonically while both control x values lie in 0–1, so bisection always converges.
        double low = 0, high = 1, s = x;
        for (var i = 0; i < 40; i++)
        {
            s = (low + high) / 2;
            if (Bezier(s, x1, x2) < x)
            {
                low = s;
            }
            else
            {
                high = s;
            }
        }

        return Bezier(s, y1, y2);
    }

    private static double Bezier(double s, double p1, double p2)
    {
        var r = 1 - s;
        return 3 * r * r * s * p1 + 3 * r * s * s * p2 + s * s * s;
    }
}
