using SemanticAgentBroker.Core.Filters;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Hubs;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.Shared.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace SemanticAgentBroker.Api.Features.Tasks.Services;

public class AgentTaskStore : IAgentTaskStore, IDisposable
{
    private readonly AppDbContext _db;
    private readonly IHubContext<AgentLogHub> _hub;
    private readonly AgentSystemOptions _systemOptions;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _disposed;

    public AgentTaskStore(AppDbContext db, IHubContext<AgentLogHub> hub, AgentSystemOptions systemOptions)
    {
        _db = db;
        _hub = hub;
        _systemOptions = systemOptions;
    }

    // ── IAgentTaskStore (used by SK Filters) ───────────────────────────────

    public async Task LogAsync(Guid sessionId, string level, string message)
    {
        await LogEventAsync(sessionId, "Log", level, message);
    }

    // ── Event Sourcing ─────────────────────────────────────────────────────

    public async Task LogEventAsync(Guid sessionId, string eventType, string level, string payload)
    {
        var evt = new TaskEvent
        {
            SessionId = sessionId,
            EventType = eventType,
            Level = level,
            Payload = payload,
            Timestamp = DateTime.UtcNow
        };

        await _dbLock.WaitAsync();
        try
        {
            _db.TaskEvents.Add(evt);
            await _db.SaveChangesAsync();
            _db.Entry(evt).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // Task session was likely deleted concurrently
            _db.ChangeTracker.Clear();
            return;
        }
        finally
        {
            _dbLock.Release();
        }

        // Broadcast as legacy ReceiveLog for backward compat
        var legacyLog = new AgentTaskLog
        {
            Id = evt.Id,
            TaskId = sessionId,
            Timestamp = evt.Timestamp,
            LogLevel = level,
            Message = payload
        };
        await _hub.Clients.Group(sessionId.ToString()).SendAsync(HubMethodNames.ReceiveLog, legacyLog);
    }

    // ── State Transitions ──────────────────────────────────────────────────

    public async Task UpdateStateAsync(Guid sessionId, TaskState newState, string? detail = null)
    {
        TaskEvent? evt = null;
        bool isPrivate = false;
        Guid? createdByUserId = null;
        await _dbLock.WaitAsync();
        try
        {
            var session = await _db.TaskSessions.FindAsync(sessionId);
            if (session == null) return;

            isPrivate = session.IsPrivate;
            createdByUserId = session.CreatedByUserId;

            session.State = newState;
            if (session.StartedAt == null && newState is TaskState.Planning or TaskState.InProgress)
                session.StartedAt = DateTime.UtcNow;

            if (newState is TaskState.Completed or TaskState.Failed or TaskState.Cancelled)
                session.CompletedAt = DateTime.UtcNow;

            // Batch the state change AND the StateChanged event log
            // into a single SaveChangesAsync instead of two separate writes.
            evt = new TaskEvent
            {
                SessionId = sessionId,
                EventType = "StateChanged",
                Level = "System",
                Payload = $"{newState}: {detail ?? ""}",
                Timestamp = DateTime.UtcNow
            };
            _db.TaskEvents.Add(evt);
            await _db.SaveChangesAsync();
            _db.Entry(evt).State = EntityState.Detached;
            _db.Entry(session).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // Task session was likely deleted concurrently
            _db.ChangeTracker.Clear();
            return;
        }
        finally
        {
            _dbLock.Release();
        }

        // Broadcast state change: All clients for public tasks, Task group + Creator group for private tasks
        var stateEvent = new TaskStateChangedEvent(sessionId, newState, detail);
        if (isPrivate)
        {
            var taskGroup = _hub.Clients?.Group(sessionId.ToString());
            if (taskGroup != null)
                await taskGroup.SendAsync(HubMethodNames.TaskStateChanged, stateEvent);

            if (createdByUserId.HasValue)
            {
                var userGroup = _hub.Clients?.Group($"user_{createdByUserId.Value}");
                if (userGroup != null)
                    await userGroup.SendAsync(HubMethodNames.TaskStateChanged, stateEvent);
            }
        }
        else
        {
            var allClients = _hub.Clients?.All;
            if (allClients != null)
            {
                await allClients.SendAsync(HubMethodNames.TaskStateChanged, stateEvent);
            }
            else
            {
                var groupClients = _hub.Clients?.Group(sessionId.ToString());
                if (groupClients != null)
                {
                    await groupClients.SendAsync(HubMethodNames.TaskStateChanged, stateEvent);
                }
            }
        }

        // Broadcast as log event too
        if (evt != null)
        {
            var legacyLog = new AgentTaskLog
            {
                Id = evt.Id,
                TaskId = sessionId,
                Timestamp = evt.Timestamp,
                LogLevel = "System",
                Message = evt.Payload
            };
            var groupClients = _hub.Clients?.Group(sessionId.ToString());
            if (groupClients != null)
                await groupClients.SendAsync(HubMethodNames.ReceiveLog, legacyLog);
        }
    }

    // ── Chat History Persistence ───────────────────────────────────────────

    public async Task SaveChatMessageAsync(Guid sessionId, int sequenceNumber, string role, string content, string? agentName = null, string? userName = null)
    {
        var snapshot = new ChatMessageSnapshot
        {
            SessionId = sessionId,
            SequenceNumber = sequenceNumber,
            Role = role,
            Content = content,
            AgentName = agentName,
            UserName = userName,
            Timestamp = DateTime.UtcNow
        };

        await _dbLock.WaitAsync();
        try
        {
            _db.ChatMessages.Add(snapshot);
            await _db.SaveChangesAsync();
            _db.Entry(snapshot).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // Task session was likely deleted concurrently
            _db.ChangeTracker.Clear();
            return;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<List<ChatMessageSnapshot>> GetChatHistoryAsync(Guid sessionId)
    {
        await _dbLock.WaitAsync();
        try
        {
            return await _db.ChatMessages
                .Where(m => m.SessionId == sessionId)
                .OrderBy(m => m.SequenceNumber)
                .ToListAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // ── Compaction Persistence ──────────────────────────────────────────────

    /// <summary>
    /// Returns the highest sequence number in the database for the given session.
    /// Used to ensure UI chat history is appended uniquely without DB primary key crashes
    /// even if the LLM sliding window truncates its own sequence memory.
    /// </summary>
    public async Task<int> GetMaxSequenceNumberAsync(Guid sessionId)
    {
        await _dbLock.WaitAsync();
        try
        {
            var hasMessages = await _db.ChatMessages.AnyAsync(m => m.SessionId == sessionId);
            if (!hasMessages) return -1;
            return await _db.ChatMessages
                .Where(m => m.SessionId == sessionId)
                .MaxAsync(m => m.SequenceNumber);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // ── Implementation Plan Persistence ────────────────────────────────────

    public async Task SavePlanAsync(Guid sessionId, int version, string planMarkdown, string? openQuestions, PlanStatus status = PlanStatus.AwaitingReview)
    {
        await _dbLock.WaitAsync();
        try
        {
            var plan = new ImplementationPlan
            {
                SessionId = sessionId,
                Version = version,
                PlanMarkdown = planMarkdown,
                OpenQuestions = openQuestions,
                Status = status,
                CreatedAt = DateTime.UtcNow,
                ApprovedAt = status == PlanStatus.Approved ? DateTime.UtcNow : null
            };
            _db.ImplementationPlans.Add(plan);
            await _db.SaveChangesAsync();
            _db.Entry(plan).State = EntityState.Detached;
        }
        catch (DbUpdateException)
        {
            // Task session was likely deleted concurrently
            _db.ChangeTracker.Clear();
            return;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task ApprovePlanAsync(Guid sessionId, int version)
    {
        await _dbLock.WaitAsync();
        try
        {
            var plan = await _db.ImplementationPlans
                .Where(p => p.SessionId == sessionId && p.Version == version)
                .FirstOrDefaultAsync();

            if (plan != null)
            {
                plan.Status = PlanStatus.Approved;
                plan.ApprovedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _db.Entry(plan).State = EntityState.Detached;
            }
        }
        catch (DbUpdateException)
        {
            // Task session or plan was likely deleted concurrently
            _db.ChangeTracker.Clear();
            return;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<ImplementationPlan?> GetLatestPlanAsync(Guid sessionId)
    {
        await _dbLock.WaitAsync();
        try
        {
            return await _db.ImplementationPlans
                .Where(p => p.SessionId == sessionId)
                .OrderByDescending(p => p.Version)
                .FirstOrDefaultAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // ── SignalR Broadcasting Helpers ────────────────────────────────────────

    public async Task BroadcastThoughtAsync(Guid sessionId, string thought, string agentName = "Agent", string? observation = null)
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.AgentThought, new AgentThoughtEvent(sessionId, thought, agentName, observation));
    }

    public async Task BroadcastPlanAsync(Guid sessionId, string planMarkdown, bool requiresApproval)
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.PlanGenerated, new PlanGeneratedEvent(sessionId, planMarkdown, requiresApproval));
    }

    public async Task BroadcastPlanReviewAsync(Guid sessionId, string planMarkdown, string? openQuestions, int version)
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.PlanReviewRequested, new PlanReviewRequestedEvent(sessionId, planMarkdown, openQuestions, version));
    }

    public async Task BroadcastPlanRevisionAsync(Guid sessionId, string planMarkdown, int version)
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.PlanRevision, new PlanRevisionEvent(sessionId, planMarkdown, version));
    }

    public async Task BroadcastToolExecutionAsync(Guid sessionId, string toolName, string args, string? result, bool isStarting, string agentName = "Orchestrator")
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.ToolExecution, new ToolExecutionEvent(sessionId, toolName, args, result, isStarting, agentName));
    }

    public async Task BroadcastActivityAsync(Guid sessionId, string activityLabel, string agentName = "Orchestrator")
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.AgentActivity, new AgentActivityEvent(sessionId, activityLabel, agentName));
    }

    public async Task BroadcastContextCompactionAsync(Guid sessionId, int originalCount, int newCount)
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.ContextCompaction, new ContextCompactionEvent(sessionId, originalCount, newCount));
    }

    public async Task BroadcastWorkspaceFileChangedAsync(Guid sessionId, string fileName, string changeType)
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.WorkspaceFileChanged, new WorkspaceFileChangedEvent(sessionId, fileName, changeType));
    }

    public async Task BroadcastTokenCountAsync(Guid sessionId, int tokenCount, int tokenLimit, string scope = "Subagent")
    {
        await _hub.Clients.Group(sessionId.ToString())
            .SendAsync(HubMethodNames.TokenCountUpdated, new TokenCountUpdatedEvent(sessionId, tokenCount, tokenLimit, scope));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    public async Task SetFinalResultAsync(Guid sessionId, string result)
    {
        await _dbLock.WaitAsync();
        try
        {
            var session = await _db.TaskSessions.FindAsync(sessionId);
            if (session != null)
            {
                session.FinalResult = result;
                await _db.SaveChangesAsync();
                _db.Entry(session).State = EntityState.Detached;
            }
        }
        catch (DbUpdateException)
        {
            // Task session was likely deleted concurrently
            _db.ChangeTracker.Clear();
            return;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<List<TaskSession>> GetExpiredTasksAsync(DateTime cutoffDate, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            return await _db.TaskSessions
                .Where(t => t.CreatedAt < cutoffDate)
                .ToListAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task DeleteTaskWithCleanupAsync(Guid taskId, bool cleanupWorkspace, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        bool shouldCleanupWorkspace = false;
        try
        {
            var session = await _db.TaskSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == taskId, ct);
            if (session != null)
            {
                // If the session exists, only delete if it is in a terminal state (Completed, Failed, Cancelled)
                if (session.State != TaskState.Completed && session.State != TaskState.Failed && session.State != TaskState.Cancelled)
                {
                    // Task was restarted or resumed to active state; protect it from deletion
                    return;
                }

                int deleted = await _db.TaskSessions
                    .Where(s => s.Id == taskId && (s.State == TaskState.Completed || s.State == TaskState.Failed || s.State == TaskState.Cancelled))
                    .ExecuteDeleteAsync(ct);

                if (deleted == 0)
                {
                    // State changed concurrently; protect task
                    return;
                }

                shouldCleanupWorkspace = true;
            }

            await _db.TaskEvents.Where(e => e.SessionId == taskId).ExecuteDeleteAsync(ct);
            await _db.ChatMessages.Where(m => m.SessionId == taskId).ExecuteDeleteAsync(ct);
            await _db.ImplementationPlans.Where(p => p.SessionId == taskId).ExecuteDeleteAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }

        if (cleanupWorkspace && shouldCleanupWorkspace)
        {
            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, _systemOptions);
            if (Directory.Exists(workspacePath))
            {
                try
                {
                    Directory.Delete(workspacePath, recursive: true);
                }
                catch
                {
                    // Ignore file lock errors during background retention sweep
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _dbLock.Dispose();
        }
    }
}
