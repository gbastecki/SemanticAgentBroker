using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Api.Features.Tasks.Services;

namespace SemanticAgentBroker.Api.Hubs;

public class AgentLogHub : Hub
{
    private readonly TaskOperationsService _taskOps;
    private readonly AuthService _authService;
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentLogHub(TaskOperationsService taskOps, AuthService authService, IServiceScopeFactory scopeFactory)
    {
        _taskOps = taskOps;
        _authService = authService;
        _scopeFactory = scopeFactory;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext != null && httpContext.Items.TryGetValue("SessionInfo", out var sessionObj) && sessionObj is SessionInfo session)
        {
            Context.Items["SessionInfo"] = session;
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{session.UserId}");
        }
        else
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    private async Task<bool> IsAuthorizedForTaskAsync(Guid taskId, bool isControlAction = false)
    {
        var session = Context.Items["SessionInfo"] as SessionInfo 
            ?? Context.GetHttpContext()?.Items["SessionInfo"] as SessionInfo;
            
        if (session == null) return false;
        if (session.Role == "Admin") return true;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Api.Data.AppDbContext>();
        var task = await db.TaskSessions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId);
        
        if (task == null) return false;

        // Non-owner cannot access private tasks
        if (task.IsPrivate && task.CreatedByUserId != session.UserId) return false;

        if (task.CreatedByUserId == session.UserId) return true;

        if (task.SourcePluginId != null && task.PluginTaskId != null)
        {
            var authDb = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Api.Data.AuthDbContext>();
            var perms = await authDb.GetEffectivePermissionsAsync(session.UserId);
            return perms.HasPermission(task.SourcePluginId, task.PluginTaskId);
        }

        // Control actions (interrupt, approve, reject, modify plan) are restricted to task owner or users with explicit plugin task permissions
        if (isControlAction)
        {
            return false;
        }

        // Public non-plugin tasks are accessible (read-only subscription) to all authenticated users
        return !task.IsPrivate;
    }

    // ── Group Management ───────────────────────────────────────────────────

    public async Task SubscribeToTask(Guid taskId)
    {
        if (!await IsAuthorizedForTaskAsync(taskId, isControlAction: false)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, taskId.ToString());
    }

    public async Task UnsubscribeFromTask(Guid taskId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, taskId.ToString());
    }

    // ── User → Agent Communication ─────────────────────────────────────────

    /// <summary>User sends an interruption message to a running task.</summary>
    public async Task<bool> SendInterruption(Guid taskId, string message)
    {
        if (!await IsAuthorizedForTaskAsync(taskId, isControlAction: true)) return false;
        var session = Context.Items["SessionInfo"] as SessionInfo ?? Context.GetHttpContext()?.Items["SessionInfo"] as SessionInfo;
        return _taskOps.SendInterruption(taskId, message, session?.Username ?? "User");
    }

    /// <summary>User approves the current pending step.</summary>
    public async Task ApproveStep(Guid taskId)
    {
        if (!await IsAuthorizedForTaskAsync(taskId, isControlAction: true)) return;
        _taskOps.ApproveStep(taskId);
    }

    /// <summary>User rejects the current pending step with feedback.</summary>
    public async Task RejectStep(Guid taskId, string feedback)
    {
        if (!await IsAuthorizedForTaskAsync(taskId, isControlAction: true)) return;
        var session = Context.Items["SessionInfo"] as SessionInfo ?? Context.GetHttpContext()?.Items["SessionInfo"] as SessionInfo;
        _taskOps.RejectStep(taskId, feedback, session?.Username ?? "User");
    }

    // ── Plan Review Actions ────────────────────────────────────────────────

    /// <summary>User approves the implementation plan.</summary>
    public async Task ApprovePlan(Guid taskId)
    {
        if (!await IsAuthorizedForTaskAsync(taskId, isControlAction: true)) return;
        _taskOps.ApprovePlan(taskId);
    }

    /// <summary>User directly modifies the plan markdown.</summary>
    public async Task ModifyPlan(Guid taskId, string modifiedPlanMarkdown)
    {
        if (!await IsAuthorizedForTaskAsync(taskId, isControlAction: true)) return;
        _taskOps.ModifyPlan(taskId, modifiedPlanMarkdown);
    }

    /// <summary>User rejects the plan with feedback for AI-driven revision.</summary>
    public async Task RejectPlan(Guid taskId, string feedback)
    {
        if (!await IsAuthorizedForTaskAsync(taskId, isControlAction: true)) return;
        _taskOps.RejectPlan(taskId, feedback);
    }

    /// <summary>User asks a question about the plan.</summary>
    public async Task AskPlanQuestion(Guid taskId, string question)
    {
        if (!await IsAuthorizedForTaskAsync(taskId, isControlAction: true)) return;
        _taskOps.AskPlanQuestion(taskId, question);
    }
}
