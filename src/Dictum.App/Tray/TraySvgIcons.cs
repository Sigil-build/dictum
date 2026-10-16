using System.Globalization;
using System.Text;

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

    public static string Name(TrayGlyph glyph, int frame = 0, bool colour = true) => glyph switch
    {
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
        var svg = new StringBuilder()
            .Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\" viewBox=\"0 0 32 32\">")
            .Append("<style id=\"current-color-scheme\" type=\"text/css\">.ColorScheme-Text { color:#9AA0AE; }</style>");
        svg.Append(glyph == TrayGlyph.Processing
            ? Invariant($"<circle {Mono} cx=\"16\" cy=\"16\" r=\"13\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"3\" stroke-linecap=\"round\" stroke-dasharray=\"{TrayGeometry.DashOn} {TrayGeometry.DashOff}\" stroke-dashoffset=\"{TrayGeometry.DashOffset(frame):0.##}\"/>")
            : $"<circle {Mono} cx=\"16\" cy=\"16\" r=\"13\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"3\"/>");
        svg.Append(glyph switch
        {
            TrayGlyph.Idle or TrayGlyph.Processing =>
                $"<path {Mono} d=\"{TrayGeometry.WavePath}\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"3\" stroke-linecap=\"round\"/>",
            TrayGlyph.Recording => $"<circle cx=\"16\" cy=\"16\" r=\"6\" {red}/>",
            TrayGlyph.Error =>
                $"<path d=\"M16 9v8.5\" {redStroke} stroke-width=\"3.2\" stroke-linecap=\"round\"/><circle cx=\"16\" cy=\"22.5\" r=\"1.9\" {red}/>",
            TrayGlyph.Paused =>
                $"<rect {Mono} x=\"11\" y=\"10\" width=\"3.4\" height=\"12\" rx=\"1.2\" fill=\"currentColor\"/>" +
                $"<rect {Mono} x=\"17.6\" y=\"10\" width=\"3.4\" height=\"12\" rx=\"1.2\" fill=\"currentColor\"/>",
            _ => throw new ArgumentOutOfRangeException(nameof(glyph)),
        });
        return svg.Append("</svg>").ToString();
    }

    private static (string Name, string Svg) Entry(TrayGlyph glyph, int frame = 0, bool colour = true) =>
        (Name(glyph, frame, colour), Svg(glyph, frame, colour));

    private static string Slug(TrayGlyph glyph) => glyph.ToString().ToLowerInvariant();

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
