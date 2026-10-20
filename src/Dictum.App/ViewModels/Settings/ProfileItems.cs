using System.Windows.Input;

namespace Dictum.App.ViewModels.Settings;

/// <summary>A profile in the Profiles list.</summary>
public sealed record ProfileItem(string Id, string Name, string Caption)
{
    public override string ToString() => Name;
}

/// <summary>
/// A language of a profile's list; <paramref name="Warned"/> when it has a closely related language beside it. The first
/// is the main one (<paramref name="IsMain"/>); <paramref name="MakeMain"/> moves another to the front.
/// </summary>
public sealed record LanguageChip(string Code, string Name, bool Warned, bool IsMain, ICommand MakeMain, ICommand Remove);

public sealed record WordChip(string Text, ICommand Remove);

/// <summary>A processor in a profile's chain, numbered from 1.</summary>
public sealed record ChainStep(string Id, string Kind, int Position, ICommand Remove);
