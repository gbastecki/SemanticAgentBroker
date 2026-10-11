namespace SemanticAgentBroker.Core.Features.Agents;

/// <summary>
/// Scoped service that maintains execution state and a dedicated isolated workspace on the host
/// for the duration of a single agent task.
/// </summary>
public interface IAgentTaskContext
{
    /// <summary>The unique task ID.</summary>
    Guid TaskId { get; }

    /// <summary>The absolute path to the task's workspace directory.</summary>
    string WorkspacePath { get; }

    /// <summary>Initializes the task context and workspace directory for the specified task ID.</summary>
    /// <param name="taskId">The unique task ID.</param>
    void Initialize(Guid taskId);

    /// <summary>Cleans up resources associated with this task context.</summary>
    void Cleanup();
}
