namespace Dictum.Core;

/// <summary>An STT provider or processor backend cannot be reached or refused the request. Maps to exit code 3.</summary>
public sealed class ProviderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
