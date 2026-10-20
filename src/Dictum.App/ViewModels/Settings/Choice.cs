namespace Dictum.App.ViewModels.Settings;

/// <summary>One entry of a dropdown or a segmented control; controls show <see cref="ToString"/>.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
