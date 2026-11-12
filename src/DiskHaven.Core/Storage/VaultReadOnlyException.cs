namespace DiskHaven.Core.Storage;

/// <summary>A change was asked for on a drive that is set to read-only.</summary>
public sealed class VaultReadOnlyException() : Exception("This drive is read-only.");
