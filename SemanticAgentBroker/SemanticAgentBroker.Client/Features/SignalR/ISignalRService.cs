using Microsoft.AspNetCore.SignalR.Client;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Client.Features.SignalR;

public interface ISignalRService : IAsyncDisposable
{
    // Events
    event Action<AgentTaskLog>? OnLogReceived;
    event Action<TaskStateChangedEvent>? OnTaskStateChanged;
    event Action<AgentThoughtEvent>? OnAgentThought;
    event Action<PlanGeneratedEvent>? OnPlanGenerated;
    event Action<ToolExecutionEvent>? OnToolExecution;
    event Action<AgentActivityEvent>? OnAgentActivity;
    event Action<HubConnectionState>? OnConnectionStateChanged;
    event Action<PlanReviewRequestedEvent>? OnPlanReviewRequested;
    event Action<PlanRevisionEvent>? OnPlanRevision;
    event Action<ContextCompactionEvent>? OnContextCompaction;
    event Action<WorkspaceFileChangedEvent>? OnWorkspaceFileChanged;
    event Action<TokenCountUpdatedEvent>? OnTokenCountUpdated;

    HubConnectionState State { get; }

    Task ConnectAsync();
    
    // Group Management
    Task SubscribeToTaskAsync(Guid taskId);
    Task UnsubscribeFromTaskAsync(Guid taskId);

    // Outbound: User → Agent
    Task<bool> SendInterruptionAsync(Guid taskId, string message);
    Task ApproveStepAsync(Guid taskId);
    Task RejectStepAsync(Guid taskId, string feedback);

    // Plan Review Actions
    Task ApprovePlanAsync(Guid taskId);
    Task ModifyPlanAsync(Guid taskId, string modifiedPlanMarkdown);
    Task RejectPlanAsync(Guid taskId, string feedback);
    Task AskPlanQuestionAsync(Guid taskId, string question);
}
