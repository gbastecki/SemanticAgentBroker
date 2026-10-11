namespace SemanticAgentBroker.Core.Constants;

/// <summary>
/// Well-known event type identifiers used in the event-sourced task log.
/// </summary>
public static class EventTypes
{
    /// <summary>Standard log message event.</summary>
    public const string Log = "Log";

    /// <summary>Debug-level log event.</summary>
    public const string Debug = "Debug";

    /// <summary>Agent thought / internal reasoning event.</summary>
    public const string Thought = "Thought";

    /// <summary>Event indicating a multi-step plan has been generated.</summary>
    public const string PlanGenerated = "PlanGenerated";

    /// <summary>Event indicating the user approved the proposed plan.</summary>
    public const string PlanApproved = "PlanApproved";

    /// <summary>Event indicating the plan was automatically approved.</summary>
    public const string PlanAutoApproved = "PlanAutoApproved";

    /// <summary>Event indicating the user modified the plan.</summary>
    public const string PlanModified = "PlanModified";

    /// <summary>Event indicating the plan was rejected by the user.</summary>
    public const string PlanRejected = "PlanRejected";

    /// <summary>Event when the agent asks a clarifying question during planning.</summary>
    public const string PlanQuestion = "PlanQuestion";

    /// <summary>Event when an error occurs during the planning phase.</summary>
    public const string PlanError = "PlanError";

    /// <summary>Event fired when a tool is executed by the agent.</summary>
    public const string ToolExecuted = "ToolExecuted";

    /// <summary>Event fired when a tool execution fails.</summary>
    public const string ToolError = "ToolError";

    /// <summary>Event containing the agent's final answer to the user.</summary>
    public const string FinalAnswer = "FinalAnswer";

    /// <summary>Event when the maximum step count is reached without completion.</summary>
    public const string MaxStepsReached = "MaxStepsReached";

    /// <summary>Event fired when task status or state transitions occur.</summary>
    public const string StateChanged = "StateChanged";

    /// <summary>Event fired when the task completes successfully.</summary>
    public const string TaskCompleted = "TaskCompleted";

    /// <summary>Event fired when the user pauses or interrupts execution.</summary>
    public const string UserInterruption = "UserInterruption";

    /// <summary>Event fired when context history is compacted / summarized.</summary>
    public const string ContextCompacted = "ContextCompacted";

    /// <summary>Event fired when a dangerous tool execution requires confirmation.</summary>
    public const string DangerousOperationGuard = "DangerousOperationGuard";

    /// <summary>Event fired when an LLM call is retried after a formatting or API error.</summary>
    public const string LlmRetry = "LlmRetry";
}
