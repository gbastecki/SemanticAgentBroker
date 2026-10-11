namespace SemanticAgentBroker.Shared.Constants;

/// <summary>
/// Constants for all API endpoints in the system to avoid magic strings.
/// </summary>
public static class ApiRoutes
{
    public const string PluginsBase = "/api/plugins";
    public const string PluginById = "/api/plugins/{id}";
    public const string PluginToggle = "/api/plugins/{id}/toggle";
    public const string PluginReload = "/api/plugins/{id}/reload";
    public const string PluginConfig = "/api/plugins/{id}/config";
    public const string PluginTasks = "/api/plugins/{id}/tasks";
    public const string PluginTaskToggle = "/api/plugins/{id}/tasks/{taskId}/toggle";
    public const string PluginsReloadAll = "/api/plugins/reload";

    public const string AgentsBase = "/api/agents";
    public const string AgentByName = "/api/agents/{name}";
    public const string AgentDuplicate = "/api/agents/{name}/duplicate";
    public const string AgentPreviewPrompt = "/api/agents/{name}/preview-prompt";

    public const string TasksBase = "/api/tasks";
    public const string TaskStats = "/api/tasks/stats";
    public const string TaskById = "/api/tasks/{taskId}";
    public const string TasksBulkDelete = "/api/tasks/bulk-delete";
    public const string TaskEvents = "/api/tasks/{taskId}/events";
    public const string TaskChat = "/api/tasks/{taskId}/chat";
    public const string TaskPlan = "/api/tasks/{taskId}/plan";
    public const string TaskPlans = "/api/tasks/{taskId}/plans";
    public const string TaskLogs = "/api/tasks/{taskId}/logs";
    public const string TaskPause = "/api/tasks/{taskId}/pause";
    public const string TaskResume = "/api/tasks/{taskId}/resume";
    public const string TaskRestart = "/api/tasks/{taskId}/restart";
    public const string TaskProceed = "/api/tasks/{taskId}/proceed";
    public const string TaskPlanApprove = "/api/tasks/{taskId}/plan/approve";
    public const string TaskPlanModify = "/api/tasks/{taskId}/plan/modify";
    public const string TaskPlanReject = "/api/tasks/{taskId}/plan/reject";
    public const string TaskPlanQuestion = "/api/tasks/{taskId}/plan/question";
    public const string TaskSendInterruption = "/api/tasks/{taskId}/interruption";
    public const string TaskApproveStep = "/api/tasks/{taskId}/step/approve";
    public const string TaskRejectStep = "/api/tasks/{taskId}/step/reject";

    // Workspace file management
    public const string TaskWorkspace = "/api/tasks/{taskId}/workspace";
    public const string TaskWorkspaceFile = "/api/tasks/{taskId}/workspace/{fileName}";
    public const string TaskWorkspaceUpload = "/api/tasks/{taskId}/workspace/upload";

    // Configuration
    public const string ConfigUploadLimit = "/api/config/upload-limit";
}
