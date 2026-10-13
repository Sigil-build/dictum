namespace Dictum.Core.Processing;

public interface ITextProcessor
{
    /// <summary>Processor instance id from the config, e.g. "cleanup-uk".</summary>
    string Id { get; }

    Task<string> ProcessAsync(string text, ProcessingContext context, CancellationToken ct);
}

/// <param name="Vocabulary">The profile's word list as one line ("Notepad++, VS Code"), or null.</param>
public sealed record ProcessingContext(string ProfileId, string? Language, string? ActiveApp, string? Vocabulary = null);
