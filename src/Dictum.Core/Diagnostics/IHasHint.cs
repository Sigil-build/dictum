namespace Dictum.Core.Diagnostics;

/// <summary>An error that knows how the user can fix it.</summary>
public interface IHasHint
{
    /// <summary>How to fix it, shown apart from the message.</summary>
    string? Hint { get; }
}
