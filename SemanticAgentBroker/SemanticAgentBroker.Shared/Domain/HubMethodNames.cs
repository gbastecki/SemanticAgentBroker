namespace SemanticAgentBroker.Shared.Domain;

/// <summary>
/// Well-known SignalR hub method names shared between server and client.
/// Eliminates magic strings that could silently break if mistyped.
/// </summary>
public static class HubMethodNames
{
    // Server → Client (broadcasts)
    public const string ReceiveLog = "ReceiveLog";
    public const string TaskStateChanged = "TaskStateChanged";
    public const string AgentThought = "AgentThought";
    public const string PlanGenerated = "PlanGenerated";
    public const string ToolExecution = "ToolExecution";
    public const string AgentActivity = "AgentActivity";
    public const string PlanReviewRequested = "PlanReviewRequested";
    public const string PlanRevision = "PlanRevision";
    public const string ContextCompaction = "ContextCompaction";
    public const string WorkspaceFileChanged = "WorkspaceFileChanged";
    public const string TokenCountUpdated = "TokenCountUpdated";

    // Client → Server (hub methods)
    public const string SubscribeToTask = "SubscribeToTask";
    public const string UnsubscribeFromTask = "UnsubscribeFromTask";
    public const string SendInterruption = "SendInterruption";
    public const string ApproveStep = "ApproveStep";
    public const string RejectStep = "RejectStep";
    public const string ApprovePlan = "ApprovePlan";
    public const string ModifyPlan = "ModifyPlan";
    public const string RejectPlan = "RejectPlan";
    public const string AskPlanQuestion = "AskPlanQuestion";
}
