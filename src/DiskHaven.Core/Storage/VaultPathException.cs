namespace DiskHaven.Core.Storage;

/// <summary>Thrown when a client-supplied path is invalid or points outside the vault.</summary>
public sealed class VaultPathException(string message) : Exception(message);
