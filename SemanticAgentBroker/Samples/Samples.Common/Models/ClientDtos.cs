using SemanticAgentBroker.Shared.Domain;

namespace Samples.Common.Models;

/// <summary>
/// Detailed state and metadata of a task session returned by GET /api/tasks/{taskId}.
/// </summary>
public class TaskSessionDetailDto
{
    public Guid Id { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public ExecutionMode Mode { get; set; }
    public bool DebugMode { get; set; }
    public bool TraceMode { get; set; }
    public TaskState State { get; set; }
    public string? SourcePluginId { get; set; }
    public string? PluginTaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FinalResult { get; set; }
    public int? MaxSteps { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public List<string> AllowedOptions { get; set; } = new();
}

/// <summary>
/// Paginated list wrapper returned by GET /api/tasks.
/// </summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// Chat message snapshot returned by GET /api/tasks/{taskId}/chat.
/// </summary>
public class ChatMessageDto
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? UserName { get; set; }
    public string? AgentName { get; set; }
}

/// <summary>
/// Authentication login response returned by POST /api/auth/login.
/// </summary>
public class LoginResultDto
{
    public string Token { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? ServerInstanceId { get; set; }
}

/// <summary>
/// Bulk delete request payload for POST /api/tasks/bulk-delete.
/// </summary>
public class BulkDeleteRequestDto
{
    public List<Guid> TaskIds { get; set; } = new();
}

/// <summary>
/// Agent definition summary returned by GET /api/agents.
/// </summary>
public class AgentSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string AccentClass { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsSeeded { get; set; }
    public DateTime CreatedAt { get; set; }
    public int MethodCount { get; set; }
    public List<string> Plugins { get; set; } = new();
}
