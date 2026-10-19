namespace Dictum.Core.Diagnostics;

public enum HealthLevel
{
    Ok,
    Warning,
    Error,
}

/// <param name="Hint">How to fix a non-OK status, e.g. the setting to change.</param>
public sealed record HealthStatus(HealthLevel Level, string Detail, string? Hint = null)
{
    public static HealthStatus Ok(string detail) => new(HealthLevel.Ok, detail);

    public static HealthStatus Warning(string detail, string? hint = null) => new(HealthLevel.Warning, detail, hint);

    public static HealthStatus Error(string detail, string? hint = null) => new(HealthLevel.Error, detail, hint);
}

/// <summary>A self-check of something external: every STT provider has one, a processor that calls a server too.</summary>
public interface IHealthCheck
{
    /// <summary>How long a check waits for the server it asks.</summary>
    static TimeSpan ServerTimeout { get; } = TimeSpan.FromSeconds(5);

    Task<HealthStatus> CheckAsync(CancellationToken ct);
}
