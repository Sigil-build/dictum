using System.Globalization;
using System.Text;
using Dictum.Platform.Linux.Tray;

namespace Dictum.App.Tray;

/// <summary>
/// The tray glyphs as SVG files for a StatusNotifierItem's icon theme path, loose in one folder. The mono parts
/// carry Plasma's <c>ColorScheme-Text</c> class with <c>currentColor</c>, so Plasma recolours them to the panel; the
/// default grey is what hosts without that mechanism (GNOME) draw. The red layer is a fixed colour, and the
/// <c>-mono</c> variants draw it in the mono colour for high contrast. No <c>-symbolic</c> suffix: GNOME would
/// force fills onto the stroked ring and wave.
/// </summary>
public static class TraySvgIcons
{
    private const string Mono = "class=\"ColorScheme-Text\"";

    /// <summary>The grey that hosts without Plasma's recolouring (GNOME) draw the mono parts in.</summary>
    private const string HostGrey = "#9AA0AE";

    public static string Name(TrayGlyph glyph, int frame = 0, bool colour = true) => glyph switch
    {
        TrayGlyph.Idle => StatusNotifierItem.IdleIcon,
        TrayGlyph.Processing => $"dictum-processing-{frame % TrayGeometry.ProcessingFrames}",
        TrayGlyph.Recording or TrayGlyph.Error when !colour => $"dictum-{Slug(glyph)}-mono",
        _ => $"dictum-{Slug(glyph)}",
    };

    public static IEnumerable<(string Name, string Svg)> All()
    {
        yield return Entry(TrayGlyph.Idle);
        yield return Entry(TrayGlyph.Recording);
        yield return Entry(TrayGlyph.Recording, colour: false);
        yield return Entry(TrayGlyph.Error);
        yield return Entry(TrayGlyph.Error, colour: false);
        yield return Entry(TrayGlyph.Paused);
        for (var frame = 0; frame < TrayGeometry.ProcessingFrames; frame++)
        {
            yield return Entry(TrayGlyph.Processing, frame);
        }
    }

    public static void WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, svg) in All())
        {
            File.WriteAllText(Path.Combine(directory, name + ".svg"), svg);
        }
    }

    public static string Svg(TrayGlyph glyph, int frame = 0, bool colour = true)
    {
        var red = colour ? $"fill=\"{TrayGeometry.ColourLayer}\"" : $"{Mono} fill=\"currentColor\"";
        var redStroke = colour ? $"stroke=\"{TrayGeometry.ColourLayer}\"" : $"{Mono} stroke=\"currentColor\"";
        var ring = Invariant(
            $"<circle {Mono} cx=\"{TrayGeometry.Centre}\" cy=\"{TrayGeometry.Centre}\" r=\"{TrayGeometry.RingRadius}\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"{TrayGeometry.Stroke}\"");
        var svg = new StringBuilder()
            .Append(Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{TrayGeometry.Grid}\" height=\"{TrayGeometry.Grid}\" viewBox=\"0 0 {TrayGeometry.Grid} {TrayGeometry.Grid}\">"))
            .Append($"<style id=\"current-color-scheme\" type=\"text/css\">.ColorScheme-Text {{ color:{HostGrey}; }}</style>");
        svg.Append(glyph == TrayGlyph.Processing
            ? Invariant($"{ring} stroke-linecap=\"round\" stroke-dasharray=\"{TrayGeometry.DashOn} {TrayGeometry.DashOff}\" stroke-dashoffset=\"{TrayGeometry.DashOffset(frame):0.##}\"/>")
            : ring + "/>");
        svg.Append(glyph switch
        {
            TrayGlyph.Idle or TrayGlyph.Processing => Invariant(
                $"<path {Mono} d=\"{TrayGeometry.WavePath}\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"{TrayGeometry.Stroke}\" stroke-linecap=\"round\"/>"),
            TrayGlyph.Recording => Invariant($"<circle cx=\"{TrayGeometry.Centre}\" cy=\"{TrayGeometry.Centre}\" r=\"{TrayGeometry.DotRadius}\" {red}/>"),
            TrayGlyph.Error => Exclamation(red, redStroke),
            TrayGlyph.Paused => PauseBar(TrayGeometry.PauseBarLeft) + PauseBar(TrayGeometry.PauseBarRight),
            _ => throw new ArgumentOutOfRangeException(nameof(glyph)),
        });
        return svg.Append("</svg>").ToString();
    }

    private static string Exclamation(string red, string redStroke) =>
        Invariant($"<path d=\"M{TrayGeometry.Centre} {TrayGeometry.ExclamationTop}v{TrayGeometry.ExclamationBottom - TrayGeometry.ExclamationTop}\" {redStroke} stroke-width=\"{TrayGeometry.ExclamationStroke}\" stroke-linecap=\"round\"/>")
        + Invariant($"<circle cx=\"{TrayGeometry.Centre}\" cy=\"{TrayGeometry.ExclamationDotY}\" r=\"{TrayGeometry.ExclamationDotRadius}\" {red}/>");

    private static string PauseBar(double left) =>
        Invariant($"<rect {Mono} x=\"{left}\" y=\"{TrayGeometry.PauseBarTop}\" width=\"{TrayGeometry.PauseBarWidth}\" height=\"{TrayGeometry.PauseBarHeight}\" rx=\"{TrayGeometry.PauseBarRadius}\" fill=\"currentColor\"/>");

    private static (string Name, string Svg) Entry(TrayGlyph glyph, int frame = 0, bool colour = true) =>
        (Name(glyph, frame, colour), Svg(glyph, frame, colour));

    private static string Slug(TrayGlyph glyph) => glyph.ToString().ToLowerInvariant();

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
