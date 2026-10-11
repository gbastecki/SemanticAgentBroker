namespace SemanticAgentBroker.Shared.Domain;

// ── Enums ──────────────────────────────────────────────────────────────────

public enum TaskState
{
    Draft,
    Queued,             // User action received, waiting for execution slot
    Planning,
    PlanReview,         // Plan generated, waiting for user review/modification
    AwaitingApproval,
    AwaitingInput,
    InProgress,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum ExecutionMode
{
    /// <summary>Generate plan → Auto-approve (no user review) → then execute autonomously. Accepts mid-flight interruptions.</summary>
    Auto,
    /// <summary>Generate plan → User reviews/approves → then execute autonomously. Accepts mid-flight interruptions.</summary>
    Planned
}

public enum PlanStatus
{
    Draft,
    AwaitingReview,
    Revised,
    Approved,
    Rejected,
    RuntimeUpdate
}

// ── Core Entities (Event-Sourced) ──────────────────────────────────────────

public class TaskSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AgentName { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public TaskState State { get; set; } = TaskState.Draft;
    public ExecutionMode Mode { get; set; } = ExecutionMode.Auto;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FinalResult { get; set; }
    public bool DebugMode { get; set; } = false;
    public bool TraceMode { get; set; } = false;
    public bool IsPrivate { get; set; } = false;
    public Guid? CreatedByUserId { get; set; }
    
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? CreatedByUserName { get; set; }
    
    public int? MaxSteps { get; set; }

    public string? PluginTaskId { get; set; }
    public string? SourcePluginId { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<string> AllowedOptions { get; set; } = new();

    public List<TaskEvent> Events { get; set; } = new();
    public List<ChatMessageSnapshot> ChatHistory { get; set; } = new();
    public List<ImplementationPlan> Plans { get; set; } = new();
}

/// <summary>
/// Append-only event log. Every action the agent takes is stored as an event
/// for full traceability and reconstruction on restart.
/// </summary>
public class TaskEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>TaskCreated, PlanGenerated, UserApproved, ToolExecuted, StepCompleted, UserInterruption, etc.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>System, Tool, Reasoning, User</summary>
    public string Level { get; set; } = "System";

    /// <summary>JSON or plain text payload.</summary>
    public string Payload { get; set; } = string.Empty;

    public string AgentName { get; set; } = "Orchestrator";

    public TaskSession? Session { get; set; }
}

/// <summary>
/// Persisted snapshot of the SK ChatHistory for recovery after restarts.
/// </summary>
public class ChatMessageSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int SequenceNumber { get; set; }

    /// <summary>system, user, assistant</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
    public string? AgentName { get; set; }
    public string? UserName { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public TaskSession? Session { get; set; }
}

/// <summary>
/// Persisted implementation plan with versioning for the planning pipeline.
/// Each revision creates a new version. User can approve, modify, or reject.
/// </summary>
public class ImplementationPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int Version { get; set; } = 1;
    public string PlanMarkdown { get; set; } = string.Empty;
    public string? OpenQuestions { get; set; }
    public PlanStatus Status { get; set; } = PlanStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public string? UserFeedback { get; set; }
    public TaskSession? Session { get; set; }
}

// ── Legacy DTO (used by SignalR ReceiveLog for backward compat) ────────────

public class AgentTaskLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string LogLevel { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Aggregated task execution statistics across all pages.
/// </summary>
public record TaskStatsDto(int TotalCount, int ActiveCount, int CompletedCount, int FailedCount);

