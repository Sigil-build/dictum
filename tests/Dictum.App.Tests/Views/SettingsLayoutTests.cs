using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dictum.App.Tests.Docs;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Tests.Views;

/// <summary>
/// Every Settings page at the window's smallest size, 800 × 560, and at its designed size, 960 × 640, in English and in
/// Ukrainian, in the world the docs' screenshots show, less the speech model's 870 MB file: no button or text runs past
/// the side of the page, of the scroll area it is in or under that area's scroll bar; no text is wider than its own box;
/// no wrapped text splits a word, such as a folder's name in a path, across two lines; and no control lies over another.
/// </summary>
public sealed class SettingsLayoutTests(AvaloniaFixture fx)
{
    /// <summary>Half a pixel for rounding.</summary>
    private const double Slack = 0.5;

    public static TheoryData<string, int, int> Windows => new()
    {
        { "en", 800, 560 },
        { "uk", 800, 560 },
        { "en", 960, 640 },
        { "uk", 960, 640 },
    };

    /// <summary>
    /// The pages the screenshots show, then the replace rules' editor, which no shot shows: a new rules processor, last,
    /// as it is saved into the world's config.
    /// </summary>
    private static readonly IReadOnlyList<ScreenshotTests.SettingsShot> Pages =
    [
        .. ScreenshotTests.SettingsShots,
        new("replace-rules", SettingsPageKey.Processors, page => ((ProcessorsPage)page).AddRulesCommand.Execute(null)),
    ];

    [Theory, MemberData(nameof(Windows))]
    public Task NothingIsCutOffAtTheSides(string language, int width, int height) => fx.UiAsync(async () =>
    {
        using var culture = new ScreenshotTests.CultureScope(language, language == "uk" ? "uk-UA" : "en-US");
        using var world = new ScreenshotTests.SettingsWorld(language, speechModel: false);
        var problems = new List<string>();
        foreach (var shot in Pages)
        {
            using var shown = await world.ShowAsync(shot, new Size(width, height));
            var page = shown.Window.FindControl<TransitioningContentControl>("PageHost")!;
            problems.AddRange(CutOff(page).Select(problem => $"{shot.Name}: {problem}"));
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problem(s) at {width} × {height} in {language}:\n{string.Join('\n', problems)}");
    });

    /// <summary>The page's visible buttons and texts that do not fit across, and its controls that lie over one another, each described.</summary>
    private static IEnumerable<string> CutOff(Control page)
    {
        var shown = page.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && !InScrollBar(c)).ToList();
        foreach (var control in shown.Where(c => c is Button or TextBlock))
        {
            var bounds = Within(control, page);
            var (left, right) = Room(control, page);
            if (bounds.Left < left - Slack || bounds.Right > right + Slack)
            {
                yield return $"{Describe(control)} spans {bounds.Left:F0}–{bounds.Right:F0}, but only {left:F0}–{right:F0} shows";
            }
            else if (control is TextBlock text && Overflows(text) is { } needed)
            {
                yield return $"{Describe(text)} needs {needed:F0} px across, but has {text.Bounds.Width:F0}";
            }
            else if (control is TextBlock wrapped && SplitWord(wrapped) is { } split)
            {
                yield return $"{Describe(wrapped)} splits a word across lines: \"{split}\"";
            }
        }

        // A control drawn over another hides part of it, as a fixed-width dropdown that runs under the button beside it.
        var controls = shown.Where(c => c is Button or ComboBox or TextBox or ListBoxItem or ToggleSwitch or NumericUpDown).ToList();
        for (var i = 0; i < controls.Count; i++)
        {
            for (var j = i + 1; j < controls.Count; j++)
            {
                var (a, b) = (controls[i], controls[j]);
                if (a.IsVisualAncestorOf(b) || b.IsVisualAncestorOf(a))
                {
                    continue;
                }

                var overlap = Within(a, page).Intersect(Within(b, page));
                if (overlap.Width > 1 && overlap.Height > 1)
                {
                    yield return $"{Describe(a)} and {Describe(b)} overlap by {overlap.Width:F0} px across";
                }
            }
        }
    }

    /// <summary>A scroll bar lies over the content on purpose; the room it takes is in <see cref="Room"/>.</summary>
    private static bool InScrollBar(Visual visual) => visual.GetVisualAncestors().Any(a => a is ScrollBar);

    /// <summary>The control's box in the page's coordinates.</summary>
    private static Rect Within(Visual visual, Visual page) =>
        new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(page) ?? Matrix.Identity);

    /// <summary>
    /// Where across the page the control can show: the page's width, narrowed by every ancestor that clips, such as a
    /// scroll area, and by the vertical scroll bar of each scroll area it is in, which widens over the content under
    /// the pointer. Only across: a control below the fold is still there to scroll to.
    /// </summary>
    private static (double Left, double Right) Room(Visual control, Visual page)
    {
        var (left, right) = (0.0, page.Bounds.Width);
        foreach (var ancestor in control.GetVisualAncestors().TakeWhile(a => a != page))
        {
            if (ancestor.ClipToBounds)
            {
                var clip = Within(ancestor, page);
                (left, right) = (Math.Max(left, clip.Left), Math.Min(right, clip.Right));
            }

            if (ancestor is ScrollViewer scroll && VerticalBar(scroll) is { } bar)
            {
                right = Math.Min(right, Within(bar, page).Left);
            }
        }

        return (left, right);
    }

    /// <summary>The scroll area's own vertical scroll bar, while it shows.</summary>
    private static ScrollBar? VerticalBar(ScrollViewer scroll) =>
        scroll.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(bar => bar.TemplatedParent == scroll && bar.Orientation == Orientation.Vertical && bar.IsEffectivelyVisible);

    /// <summary>The width a text without trimming needs, when that is more than its box has; null when it fits.</summary>
    private static double? Overflows(TextBlock text)
    {
        if (!Equals(text.TextTrimming, TextTrimming.None))
        {
            return null;
        }

        var needed = text.TextLayout.Width + text.Padding.Left + text.Padding.Right;
        return needed > text.Bounds.Width + Slack ? needed : null;
    }

    /// <summary>The two halves of a word that a wrapped text breaks between lines, such as "Dict|um"; null when every line ends between words.</summary>
    private static string? SplitWord(TextBlock text)
    {
        if (text.Text is not { } content)
        {
            return null;
        }

        foreach (var line in text.TextLayout.TextLines.SkipLast(1))
        {
            var end = line.FirstTextSourceIndex + line.Length;
            if (end > 0 && end < content.Length && char.IsLetterOrDigit(content[end - 1]) && char.IsLetterOrDigit(content[end]))
            {
                var (start, stop) = (end, end);
                while (start > 0 && char.IsLetterOrDigit(content[start - 1]))
                {
                    start--;
                }

                while (stop < content.Length && char.IsLetterOrDigit(content[stop]))
                {
                    stop++;
                }

                return $"{content[start..end]}|{content[end..stop]}";
            }
        }

        return null;
    }

    private static string Describe(Control control) => control switch
    {
        TextBlock text => $"{Kind(text)} \"{text.Text}\"",
        Button { Content: string label } => $"Button \"{label}\"",
        Button button => $"Button \"{AutomationProperties.GetName(button)}\"",
        _ => control.GetType().Name,
    };

    /// <summary>A text's own type, and the control whose template draws it, such as a TextBox's placeholder.</summary>
    private static string Kind(TextBlock text) =>
        text.TemplatedParent is Control owner ? $"{text.GetType().Name} in {owner.GetType().Name}" : text.GetType().Name;
}
