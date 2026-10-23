namespace HomeVault.Core.Storage;

/// <summary>Thrown when an operation would overwrite something or is otherwise not allowed in the current state.</summary>
public sealed class VaultConflictException(string message) : Exception(message);
