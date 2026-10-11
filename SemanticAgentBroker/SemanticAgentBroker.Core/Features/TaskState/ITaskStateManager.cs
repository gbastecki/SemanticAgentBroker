namespace SemanticAgentBroker.Core.Features.TaskState;

/// <summary>
/// Snapshot of the structured task state, read from _task_state.md.
/// </summary>
/// <param name="Objective">The high-level task objective.</param>
/// <param name="CompletedSteps">List of completed execution steps.</param>
/// <param name="FilesManifest">Dictionary mapping workspace file paths to descriptions.</param>
/// <param name="KeyFacts">List of key facts learned during execution.</param>
/// <param name="CompactionCount">Number of times context has been compacted.</param>
/// <param name="CurrentStatus">Current task status text.</param>
/// <param name="RecentObservations">List of recent raw tool observations.</param>
/// <param name="UserDirectives">List of user interruptions/directives that take permanent precedence.</param>
public record TaskStateSnapshot(
    string Objective,
    List<CompletedStep> CompletedSteps,
    Dictionary<string, string> FilesManifest,
    List<string> KeyFacts,
    int CompactionCount,
    string? CurrentStatus,
    List<RecentObservation>? RecentObservations = null,
    List<string>? UserDirectives = null)
{
    /// <summary>
    /// Computed property for backward compatibility and quick access to the latest observation.
    /// </summary>
    public string? LastRawObservation => RecentObservations?.LastOrDefault()?.Content;
}

/// <summary>
/// Represents a recent tool observation recorded in the task state.
/// </summary>
/// <param name="StepNumber">The execution step number.</param>
/// <param name="Action">The tool / action name.</param>
/// <param name="ActionInput">The arguments passed to the tool.</param>
/// <param name="Content">The tool output content.</param>
public record RecentObservation(int StepNumber, string Action, string ActionInput, string Content);

/// <summary>
/// Represents a completed execution step summary in the task state.
/// </summary>
/// <param name="StepNumber">The execution step number.</param>
/// <param name="Action">The tool / action name executed.</param>
/// <param name="ActionInput">The arguments passed to the tool.</param>
/// <param name="ResultSummary">Concise summary of the result.</param>
/// <param name="IsError">Whether this step ended in an error.</param>
public record CompletedStep(int StepNumber, string Action, string ActionInput, string ResultSummary, bool IsError = false);

/// <summary>
/// System-managed structured task state engine.
/// The engine (not the LLM) deterministically tracks completed steps, workspace files,
/// and key facts. This state survives context compaction and is injected into every LLM call.
/// </summary>
public interface ITaskStateManager
{
    /// <summary>Initializes the task state with the objective if it's currently empty.</summary>
    Task InitializeStateAsync(Guid sessionId, string objective, CancellationToken ct);

    /// <summary>Appends a new follow-up objective to the existing task state.</summary>
    Task AppendObjectiveAsync(Guid sessionId, string newObjective, CancellationToken ct);

    /// <summary>Reads the current structured task state from the workspace file.</summary>
    Task<TaskStateSnapshot> ReadStateAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the highest step number recorded in the state.
    /// </summary>
    Task<int> GetMaxStepNumberAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Records a completed step. Called automatically by the engine after each successful tool call.</summary>
    Task RecordStepAsync(Guid sessionId, int step, string action, string actionInput, string resultSummary, CancellationToken ct);

    /// <summary>Re-scans the workspace directory and updates the files manifest in the state.</summary>
    Task UpdateFilesManifestAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Increments the compaction counter in the state.</summary>
    Task IncrementCompactionCountAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Generates a compact context injection string for the LLM.
    /// Injected as a system message before each LLM call.
    /// </summary>
    Task<string> BuildContextInjectionAsync(
        Guid sessionId, int currentStep,
        int tokensUsed, int maxTokens, int compactionCount,
        CancellationToken ct);

    /// <summary>
    /// Finalizes the task state and walkthrough files before task completion.
    /// Ensures follow-up tasks have complete context.
    /// </summary>
    Task FinalizeStateAsync(Guid sessionId, string finalResult, CancellationToken ct);

    /// <summary>
    /// Removes internal session tracking state (locks, caches).
    /// Called in finally blocks to prevent leaks when FinalizeStateAsync is never reached
    /// (e.g., task failure or cancellation).
    /// </summary>
    void CleanupSession(Guid sessionId);
}
