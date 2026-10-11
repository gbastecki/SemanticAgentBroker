namespace SemanticAgentBroker.Sdk;

/// <summary>
/// Manages synchronization locks for workspace file access to prevent concurrent read/write corruptions.
/// </summary>
public interface IWorkspaceLockManager
{
    /// <summary>
    /// Acquires an asynchronous lock for a specific file (or the whole workspace if fileName is null) within a task session.
    /// </summary>
    Task<IDisposable> AcquireLockAsync(Guid sessionId, string? fileName = null, CancellationToken ct = default);

    /// <summary>
    /// Cleans up and releases locks for a completed or deleted session.
    /// </summary>
    void CleanupSession(Guid sessionId);
}
