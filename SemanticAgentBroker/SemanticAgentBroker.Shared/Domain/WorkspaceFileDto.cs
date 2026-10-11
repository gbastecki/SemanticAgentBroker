namespace SemanticAgentBroker.Shared.Domain;

/// <summary>
/// Represents a file in a task's workspace directory.
/// </summary>
public record WorkspaceFileDto(
    string Name,
    long SizeBytes,
    DateTime LastModified,
    bool IsTextFile);
