using System.Globalization;
using Dictum.App.Resources;

namespace Dictum.App.Shell;

/// <summary>
/// <c>dictum-gui --self-test</c>: checks what a Native AOT build could have dropped, prints one line per check and
/// exits 0 when all pass. CI runs it after every publish.
/// </summary>
internal static class SelfTest
{
    public static int Run(TextWriter output)
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

        UiCulture.Apply("uk");
        Check("ui culture", Strings.MenuQuit == ukrainian, $"\"{Strings.MenuQuit}\" with {CultureInfo.CurrentUICulture.Name}");
        return failures == 0 ? 0 : 1;
    }
}
