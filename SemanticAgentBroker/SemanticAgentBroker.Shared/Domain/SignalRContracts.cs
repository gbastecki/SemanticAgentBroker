namespace SemanticAgentBroker.Shared.Domain;

/// <summary>Broadcast when a task transitions to a new state.</summary>
public record TaskStateChangedEvent(Guid TaskId, TaskState NewState, string? Detail);

/// <summary>Broadcast each time the agent emits a Thought.</summary>
public record AgentThoughtEvent(Guid TaskId, string Thought, string AgentName = "Agent", string? Observation = null);

/// <summary>Broadcast when the agent generates a plan (especially for Mode 2b).</summary>
public record PlanGeneratedEvent(Guid TaskId, string PlanMarkdown, bool RequiresApproval);

/// <summary>Broadcast when a tool starts or finishes execution.</summary>
public record ToolExecutionEvent(Guid TaskId, string ToolName, string Args, string? Result, bool IsStarting, string AgentName = "Orchestrator");

/// <summary>Broadcast for the activity visualizer in the UI (spinner labels).</summary>
public record AgentActivityEvent(Guid TaskId, string ActivityLabel, string AgentName = "Orchestrator");

// ── Planning Pipeline Events ───────────────────────────────────────────────

/// <summary>Broadcast when the planning engine generates a plan for user review.</summary>
public record PlanReviewRequestedEvent(Guid TaskId, string PlanMarkdown, string? OpenQuestions, int Version);

/// <summary>Broadcast when the agent revises the plan after user feedback.</summary>
public record PlanRevisionEvent(Guid TaskId, string PlanMarkdown, int Version);

/// <summary>Broadcast when context compaction occurs to keep the user informed.</summary>
public record ContextCompactionEvent(Guid TaskId, int OriginalMessageCount, int CompactedMessageCount);

/// <summary>Broadcast when a file is created, modified, or deleted in a task workspace.</summary>
public record WorkspaceFileChangedEvent(Guid TaskId, string FileName, string ChangeType);

/// <summary>Broadcast when the token count is updated during execution.</summary>
public record TokenCountUpdatedEvent(Guid TaskId, int TokenCount, int TokenLimit, string Scope = "Subagent");
