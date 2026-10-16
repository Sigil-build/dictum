using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Dictum.App.Resources;
using Dictum.App.Tray;

namespace Dictum.App.Shell;

/// <summary>
/// <c>dictum-gui --self-test</c>: checks what a Native AOT build could have dropped, prints one line per check and
/// returns 0 when all pass. CI runs it after every publish.
/// </summary>
public static class SelfTest
{
    /// <param name="avalonia">
    /// False skips the checks that need a process of their own: Avalonia's platform (tests already run one) and the UI
    /// culture (changing it would leak into tests running in parallel).
    /// </param>
    public static int Run(TextWriter output, bool avalonia = true)
    {
        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            output.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: {detail}");
            failures += ok ? 0 : 1;
        }

        var english = Strings.ResourceManager.GetString(nameof(Strings.MenuQuit), CultureInfo.InvariantCulture) ?? "";
        var ukrainian = Strings.ResourceManager.GetString(nameof(Strings.MenuQuit), CultureInfo.GetCultureInfo("uk")) ?? "";
        Check("strings", english == "Quit Dictum" && ukrainian.Any(c => c is >= 'А' and <= 'я'), $"en \"{english}\", uk \"{ukrainian}\"");

        var icons = Path.Combine(Path.GetTempPath(), $"dictum-self-test-{Environment.ProcessId}");
        try
        {
            TraySvgIcons.WriteAll(icons);
            var files = Directory.GetFiles(icons, "*.svg").Length;
            Check("svg icons", files == 11, $"{files} files");
        }
        finally
        {
            Directory.Delete(icons, recursive: true);
        }

        if (avalonia)
        {
            Program.BuildAvaloniaApp().SetupWithoutStarting();
            var app = Application.Current!;
            var dark = app.TryGetResource("color.bg.base", ThemeVariant.Dark, out var darkBrush) ? darkBrush : null;
            var light = app.TryGetResource("color.bg.base", ThemeVariant.Light, out var lightBrush) ? lightBrush : null;
            Check("tokens", dark is ISolidColorBrush d && light is ISolidColorBrush l && d.Color != l.Color, "color.bg.base in both themes");

            var drawn = 0;
            var empty = new List<string>();
            foreach (var glyph in Enum.GetValues<TrayGlyph>())
            {
                foreach (var size in TrayGeometry.Sizes)
                {
                    using var bitmap = TrayIconRenderer.Render(glyph, 0, size, Color.Parse(TrayGeometry.MonoOnDark), colourLayer: true);
                    _ = new WindowIcon(bitmap);
                    drawn++;
                    if (!HasInk(bitmap))
                    {
                        empty.Add($"{glyph}@{size}");
                    }
                }
            }

            Check("tray icons", empty.Count == 0, empty.Count == 0 ? $"{drawn} bitmaps" : $"empty: {string.Join(", ", empty)}");

            UiCulture.Apply("uk");
            Check("ui culture", Strings.MenuQuit == ukrainian, $"\"{Strings.MenuQuit}\" with {CultureInfo.CurrentUICulture.Name}");
        }

        return failures == 0 ? 0 : 1;
    }

    private static bool HasInk(Bitmap bitmap)
    {
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var pixels = new byte[width * height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 255)
            {
                return true;
            }
        }

        return false;
    }
}
