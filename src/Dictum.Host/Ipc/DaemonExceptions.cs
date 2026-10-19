namespace Dictum.Host.Ipc;

/// <summary>The daemon is not running (no socket, or nobody listening). Maps to exit code 4.</summary>
public sealed class DaemonNotRunningException(string message) : Exception(message);

/// <summary>Another daemon already owns the socket.</summary>
public sealed class DaemonAlreadyRunningException(string message) : Exception(message);
