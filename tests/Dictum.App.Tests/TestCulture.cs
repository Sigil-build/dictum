using System.Globalization;
using System.Runtime.CompilerServices;

namespace Dictum.App.Tests;

internal static class TestCulture
{
    /// <summary>Tests compare English text and formats, whatever the machine's language (the user's is Ukrainian).</summary>
    [ModuleInitializer]
    internal static void UseEnglish()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
    }
}
