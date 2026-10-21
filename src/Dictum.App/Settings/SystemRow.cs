using System.Windows.Input;
using Dictum.App.Resources;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Settings;

/// <summary>
/// One row of Settings → System: a badge, a name with a short status under it, a detail, an
/// optional fix button, and an optional "How to fix" link under the status.
/// </summary>
/// <param name="Help">Opens the documentation that explains the problem; null on a row that has none.</param>
public sealed record SystemRow(
    string Name, HealthLevel Level, string Status, string? Detail = null, string? FixLabel = null, ICommand? Fix = null, ICommand? Help = null)
{
    /// <summary>The "How to fix" link's accessible name, "How to fix: Paste", so a screen reader's list of links tells the rows apart.</summary>
    public string HelpName => $"{Strings.SettingsSystemHelp}: {Name}";
}
