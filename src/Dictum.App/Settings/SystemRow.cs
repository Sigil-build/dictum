using System.Windows.Input;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Settings;

/// <summary>
/// One row of Settings → System: a badge, a name with a short status under it, a detail, and an
/// optional fix button.
/// </summary>
public sealed record SystemRow(string Name, HealthLevel Level, string Status, string? Detail = null, string? FixLabel = null, ICommand? Fix = null);
