namespace Dictum.Core.Diagnostics;

public enum HealthLevel
{
    Ok,
    Warning,
    Error,
}

/// <param name="Hint">What the user can do to fix a non-OK status.</param>
public sealed record HealthStatus(HealthLevel Level, string Detail, string? Hint = null)
{
    public static HealthStatus Ok(string detail) => new(HealthLevel.Ok, detail);

    public static HealthStatus Warning(string detail, string? hint = null) => new(HealthLevel.Warning, detail, hint);

    public static HealthStatus Error(string detail, string? hint = null) => new(HealthLevel.Error, detail, hint);
}

/// <summary>Optional self-check for providers and processors that talk to something external.</summary>
public interface IHealthCheck
{
    Task<HealthStatus> CheckAsync(CancellationToken ct);
}
