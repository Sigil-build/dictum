namespace Dictum.Platform;

public enum NotificationKind
{
    Info,
    Error,
}

/// <summary>Desktop notifications; <see cref="NullNotifier"/> where there is no cheap way to show them.</summary>
public interface INotifier
{
    Task NotifyAsync(string summary, string body, NotificationKind kind, CancellationToken ct);
}

public sealed class NullNotifier : INotifier
{
    public static NullNotifier Instance { get; } = new();

    public Task NotifyAsync(string summary, string body, NotificationKind kind, CancellationToken ct) => Task.CompletedTask;
}
