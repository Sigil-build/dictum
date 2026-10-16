using System.Globalization;
using Dictum.Core.Configuration;

namespace Dictum.App.Shell;

/// <summary>The UI language (spec 7): <c>general.uiLanguage</c>, or the OS UI culture when that is null.</summary>
public static class UiCulture
{
    public static CultureInfo? Resolve(string? uiLanguage) =>
        uiLanguage is "en" or "uk" ? CultureInfo.GetCultureInfo(uiLanguage) : null;

    /// <summary>Sets the UI culture for this thread and every later one. Call in Main, before Avalonia starts.</summary>
    public static void Apply(string? uiLanguage)
    {
        if (Resolve(uiLanguage) is { } culture)
        {
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    /// <summary><c>general.uiLanguage</c> from the config file; null when there is no file or it cannot be read.</summary>
    public static string? FromConfig(ConfigStore store)
    {
        if (!store.Exists)
        {
            return null;
        }

        try
        {
            return store.Load().General.UiLanguage;
        }
        catch (Exception ex) when (ex is ConfigException or IOException)
        {
            return null;
        }
    }
}
