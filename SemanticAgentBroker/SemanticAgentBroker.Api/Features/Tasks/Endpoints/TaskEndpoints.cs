using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Api.Features.Tasks.Models;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Constants;
using SemanticAgentBroker.Shared.Domain;

namespace SemanticAgentBroker.Api.Features.Tasks.Endpoints;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        // Create a new task (supports application/json and multipart/form-data for atomic file staging)
        app.MapPost(ApiRoutes.TasksBase, async (HttpRequest request, HttpContext context, AppDbContext db, AuthDbContext authDb, AgentTaskQueueService queue, IAgentRegistry registry, AgentSystemOptions sysOptions, AgentTaskStore taskStore) =>
        {
            AgentTaskRequest req;
            IFormFileCollection? files = null;

            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync();
                if (form.TryGetValue("request", out var requestJson) && !string.IsNullOrWhiteSpace(requestJson))
                {
                    req = System.Text.Json.JsonSerializer.Deserialize<AgentTaskRequest>(
                        requestJson.ToString(),
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AgentTaskRequest();
                }
                else
                {
                    req = new AgentTaskRequest
                    {
                        TaskId = form.TryGetValue("taskId", out var tid) && Guid.TryParse(tid.ToString(), out var parsedTid) ? parsedTid : Guid.Empty,
                        AgentName = form.TryGetValue("agentName", out var an) && !string.IsNullOrWhiteSpace(an) ? an.ToString() : "RuntimeAgent",
                        Prompt = form.TryGetValue("prompt", out var pr) ? pr.ToString() : string.Empty,
                        Mode = form.TryGetValue("mode", out var md) && Enum.TryParse<ExecutionMode>(md.ToString(), true, out var modeVal) ? modeVal : ExecutionMode.Planned,
                        DebugMode = form.TryGetValue("debugMode", out var dbg) && bool.TryParse(dbg.ToString(), out var dbgVal) && dbgVal,
                        TraceMode = form.TryGetValue("traceMode", out var trc) && bool.TryParse(trc.ToString(), out var trcVal) && trcVal,
                        IsPrivate = form.TryGetValue("isPrivate", out var priv) && bool.TryParse(priv.ToString(), out var privVal) && privVal,
                        SourcePluginId = form.TryGetValue("sourcePluginId", out var sp) && !string.IsNullOrWhiteSpace(sp) ? sp.ToString() : null,
                        PluginTaskId = form.TryGetValue("pluginTaskId", out var pt) && !string.IsNullOrWhiteSpace(pt) ? pt.ToString() : null,
                        MaxSteps = form.TryGetValue("maxSteps", out var ms) && int.TryParse(ms.ToString(), out var msVal) ? msVal : null
                    };
                }
                files = form.Files;
            }
            else
            {
                req = await request.ReadFromJsonAsync<AgentTaskRequest>() ?? new AgentTaskRequest();
            }

            // Validate required fields
            if (string.IsNullOrWhiteSpace(req.AgentName))
                req.AgentName = "RuntimeAgent";
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest("Prompt is required.");
            
            // In Task-Centric architecture, AgentName can be a dynamic fallback ("RuntimeAgent")
            // Only strictly require it in the registry if not using a Plugin Task template
            if (string.IsNullOrEmpty(req.PluginTaskId) && !registry.Contains(req.AgentName))
                return Results.BadRequest($"Agent '{req.AgentName}' not found. Use GET /api/agents to list available agents.");
            if (req.MaxSteps.HasValue && req.MaxSteps.Value < 1)
                return Results.BadRequest("MaxSteps must be at least 1.");

            var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            if (session != null && session.Role == "User")
            {
                if (string.IsNullOrEmpty(req.SourcePluginId) || string.IsNullOrEmpty(req.PluginTaskId))
                    return Results.Forbid();

                var allPerms = await authDb.GetEffectivePermissionsAsync(session.UserId);
                if (!allPerms.HasPermission(req.SourcePluginId, req.PluginTaskId))
                    return Results.Forbid();

                var allowedOptions = allPerms.GetAllowedOptions(req.SourcePluginId, req.PluginTaskId);

                // If user attached initial files, ensure they have UploadFiles permission
                if (files != null && files.Count > 0 && !allowedOptions.Contains("UploadFiles", StringComparer.OrdinalIgnoreCase))
                    return Results.Forbid();

                // Force options to not exceed permissions
                if (!allowedOptions.Contains("DebugMode", StringComparer.OrdinalIgnoreCase)) req.DebugMode = false;
                if (!allowedOptions.Contains("TraceMode", StringComparer.OrdinalIgnoreCase)) req.TraceMode = false;
                if (!allowedOptions.Contains("AutoExecution", StringComparer.OrdinalIgnoreCase) && req.Mode == ExecutionMode.Auto)
                    return Results.BadRequest("You do not have permission for AutoExecution on this task.");

                req.RestrictedMethodIds = null;
            }

            req.UserName = session?.Username;
            if (req.TaskId == Guid.Empty) req.TaskId = Guid.NewGuid();

            string? createdWorkspacePath = null;
            try
            {
                // Save initial files to workspace BEFORE enqueuing task
                if (files != null && files.Count > 0)
                {
                    var maxUploadSize = (long)sysOptions.MaxUploadSizeMb * 1024 * 1024;

                    // Validate all files before creating directory or writing to disk
                    foreach (var file in files)
                    {
                        if (file.Length > maxUploadSize)
                            return Results.BadRequest($"File '{file.FileName}' exceeds the {sysOptions.MaxUploadSizeMb} MB size limit.");

                        var safeName = Path.GetFileName(file.FileName);
                        if (string.IsNullOrWhiteSpace(safeName) || safeName.Contains("..") || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                            return Results.BadRequest($"Invalid file name: '{file.FileName}'.");
                    }

                    var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(req.TaskId, sysOptions);
                    Directory.CreateDirectory(workspacePath);
                    createdWorkspacePath = workspacePath;

                    foreach (var file in files)
                    {
                        var safeName = Path.GetFileName(file.FileName);
                        var targetPath = Path.Combine(workspacePath, safeName);
                        using (var stream = File.Create(targetPath))
                        {
                            await file.CopyToAsync(stream);
                        }

                        await taskStore.BroadcastWorkspaceFileChangedAsync(req.TaskId, safeName, "Created");
                    }
                }

                db.TaskSessions.Add(new TaskSession
                {
                    Id = req.TaskId,
                    AgentName = req.AgentName,
                    UserPrompt = req.Prompt,
                    Mode = req.Mode,
                    DebugMode = req.DebugMode,
                    TraceMode = req.TraceMode,
                    MaxSteps = req.MaxSteps,
                    State = TaskState.Draft,
                    PluginTaskId = req.PluginTaskId,
                    SourcePluginId = req.SourcePluginId,
                    IsPrivate = req.IsPrivate,
                    CreatedByUserId = session?.UserId
                });

                db.ChatMessages.Add(new ChatMessageSnapshot
                {
                    SessionId = req.TaskId,
                    SequenceNumber = 0,
                    Role = "user",
                    Content = req.Prompt,
                    UserName = session?.Username,
                    Timestamp = DateTime.UtcNow
                });

                await db.SaveChangesAsync();
            }
            catch
            {
                if (createdWorkspacePath != null && Directory.Exists(createdWorkspacePath))
                {
                    try { Directory.Delete(createdWorkspacePath, recursive: true); } catch { }
                }
                throw;
            }

            var reqForQueue = new AgentTaskRequest
            {
                TaskId = req.TaskId,
                AgentName = req.AgentName,
                Prompt = req.Prompt,
                Mode = req.Mode,
                DebugMode = req.DebugMode,
                TraceMode = req.TraceMode,
                IsPrivate = req.IsPrivate,
                UserName = session?.Username,
                RestrictedMethodIds = req.RestrictedMethodIds,
                IsContinuation = req.IsContinuation,
                MaxSteps = req.MaxSteps,
                PluginTaskId = req.PluginTaskId,
                SourcePluginId = req.SourcePluginId
            };
            
            await queue.EnqueueTaskAsync(reqForQueue);
            return Results.Ok(req);
        }).DisableAntiforgery();

        // Get all tasks (paginated)
        app.MapGet(ApiRoutes.TasksBase, async (HttpContext context, AppDbContext db, AuthDbContext authDb, int page = 1, int pageSize = 20) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;

            List<TaskSession> items;
            int totalCount;

            if (session != null && session.Role == "User")
            {
                var allowedTasks = await authDb.GetEffectivePermissionsAsync(session.UserId);

                // Extract wildcard and concrete permission components for SQL-side filter
                var wildcardPluginIds = allowedTasks
                    .Where(x => x.PluginTaskId == "*")
                    .Select(x => x.PluginId)
                    .ToList();
                var concretePairs = allowedTasks
                    .Where(x => x.PluginTaskId != "*")
                    .Select(x => x.PluginId + ":" + x.PluginTaskId)
                    .ToList();

                var query = db.TaskSessions.AsNoTracking()
                    .Where(t => t.SourcePluginId != null && t.PluginTaskId != null)
                    .Where(t => !t.IsPrivate || t.CreatedByUserId == session.UserId)
                    .Where(t =>
                        wildcardPluginIds.Contains(t.SourcePluginId!) ||
                        concretePairs.Contains(t.SourcePluginId! + ":" + t.PluginTaskId!)
                    );

                totalCount = await query.CountAsync();
                items = await query.OrderByDescending(t => t.CreatedAt)
                    .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            }
            else
            {
                totalCount = await db.TaskSessions.CountAsync();
                items = await db.TaskSessions.OrderByDescending(t => t.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            }

            var userIds = items.Where(i => i.CreatedByUserId.HasValue).Select(i => i.CreatedByUserId!.Value).Distinct().ToList();
            var users = await authDb.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username);
            foreach (var item in items)
            {
                if (item.CreatedByUserId.HasValue && users.TryGetValue(item.CreatedByUserId.Value, out var username))
                {
                    item.CreatedByUserName = username;
                }
            }

            return Results.Ok(new { items, totalCount, page, pageSize, totalPages = (int)Math.Ceiling((double)totalCount / pageSize) });
        });

        // Get aggregated task execution stats across all tasks
        app.MapGet(ApiRoutes.TaskStats, async (HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            IQueryable<TaskSession> query = db.TaskSessions.AsNoTracking();

            if (session != null && session.Role == "User")
            {
                var allowedTasks = await authDb.GetEffectivePermissionsAsync(session.UserId);
                var wildcardPluginIds = allowedTasks
                    .Where(x => x.PluginTaskId == "*")
                    .Select(x => x.PluginId)
                    .ToList();
                var concretePairs = allowedTasks
                    .Where(x => x.PluginTaskId != "*")
                    .Select(x => x.PluginId + ":" + x.PluginTaskId)
                    .ToList();

                query = query
                    .Where(t => t.SourcePluginId != null && t.PluginTaskId != null)
                    .Where(t => !t.IsPrivate || t.CreatedByUserId == session.UserId)
                    .Where(t =>
                        wildcardPluginIds.Contains(t.SourcePluginId!) ||
                        concretePairs.Contains(t.SourcePluginId! + ":" + t.PluginTaskId!)
                    );
            }

            var stateCounts = await query
                .GroupBy(t => t.State)
                .Select(g => new { State = g.Key, Count = g.Count() })
                .ToListAsync();

            int total = stateCounts.Sum(x => x.Count);
            int active = stateCounts
                .Where(x => x.State is TaskState.InProgress or TaskState.AwaitingApproval or TaskState.AwaitingInput or TaskState.Planning or TaskState.PlanReview or TaskState.Queued)
                .Sum(x => x.Count);
            int completed = stateCounts.Where(x => x.State == TaskState.Completed).Sum(x => x.Count);
            int failed = stateCounts.Where(x => x.State == TaskState.Failed).Sum(x => x.Count);

            return Results.Ok(new TaskStatsDto(total, active, completed, failed));
        });

        // Get single task detail
        app.MapGet(ApiRoutes.TaskById, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var session = await db.TaskSessions.FindAsync(taskId);
            if (session == null) return Results.NotFound();

            var userSession = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            List<string> allowedOptions = new() { "AutoExecution", "UploadFiles", "EditFiles", "ConsoleView", "DebugMode", "TraceMode", "Delete" }; // Admins have all

            if (userSession != null && userSession.Role != "Admin")
            {
                if (session.IsPrivate && session.CreatedByUserId != userSession.UserId)
                    return Results.Forbid();

                if (session.SourcePluginId != null && session.PluginTaskId != null)
                {
                    var allPerms = await authDb.GetEffectivePermissionsAsync(userSession.UserId);
                    if (!allPerms.HasPermission(session.SourcePluginId, session.PluginTaskId) && session.CreatedByUserId != userSession.UserId)
                        return Results.Forbid();

                    if (allPerms.HasPermission(session.SourcePluginId, session.PluginTaskId))
                        allowedOptions = allPerms.GetAllowedOptions(session.SourcePluginId, session.PluginTaskId);
                }
                else if (session.CreatedByUserId != userSession.UserId)
                {
                    return Results.Forbid();
                }
            }

            string? createdByUserName = null;
            if (session.CreatedByUserId.HasValue)
            {
                var user = await authDb.Users.FindAsync(session.CreatedByUserId.Value);
                createdByUserName = user?.Username;
            }

            return Results.Ok(new
            {
                session.Id,
                session.AgentName,
                session.UserPrompt,
                session.Mode,
                session.DebugMode,
                session.TraceMode,
                session.State,
                session.SourcePluginId,
                session.PluginTaskId,
                session.CreatedAt,
                session.StartedAt,
                session.CompletedAt,
                session.FinalResult,
                session.MaxSteps,
                session.CreatedByUserId,
                CreatedByUserName = createdByUserName,
                AllowedOptions = allowedOptions
            });
        });

        // Bulk delete tasks with all associated data
        app.MapPost(ApiRoutes.TasksBulkDelete, async (BulkDeleteRequest req, HttpContext context, AppDbContext db, TaskOperationsService taskOps, AgentTaskQueueService queue, AgentSystemOptions sysOptions, SemanticAgentBroker.Core.Features.TaskState.ITaskStateManager taskStateManager, SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager lockManager) =>
        {
            var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            if (session?.Role != "Admin") return Results.Forbid();

            if (req.TaskIds == null || req.TaskIds.Count == 0)
                return Results.BadRequest("No task IDs provided.");

            if (req.TaskIds.Count > 500)
                return Results.BadRequest("Cannot delete more than 500 tasks in a single batch request.");

            // Cancel any queued and active tasks first and await their graceful termination
            foreach (var taskId in req.TaskIds)
            {
                queue.CancelQueuedTask(taskId);
                if (taskOps.IsTaskActive(taskId))
                    taskOps.CancelTask(taskId);
            }
            var opsStopTasks = req.TaskIds.Select(taskId => taskOps.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5)));
            var queueStopTasks = req.TaskIds.Select(taskId => queue.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5)));
            var stopResults = await Task.WhenAll(opsStopTasks.Concat(queueStopTasks));
            if (stopResults.Any(stopped => !stopped))
                return Results.Conflict("One or more active tasks could not be stopped within the timeout. Please try again.");

            // Batch all deletes using WHERE..IN instead of per-task FindAsync loop
            var taskIdSet = req.TaskIds.ToHashSet();

            await db.TaskEvents.Where(e => taskIdSet.Contains(e.SessionId)).ExecuteDeleteAsync();
            await db.ChatMessages.Where(m => taskIdSet.Contains(m.SessionId)).ExecuteDeleteAsync();
            await db.ImplementationPlans.Where(p => taskIdSet.Contains(p.SessionId)).ExecuteDeleteAsync();
            await db.TaskSessions.Where(s => taskIdSet.Contains(s.Id)).ExecuteDeleteAsync();

            // Best-effort workspace cleanup (outside DB transaction)
            if (sysOptions.CleanupWorkspacesOnTaskDelete)
            {
                foreach (var taskId in req.TaskIds)
                {
                    var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
                    if (Directory.Exists(workspacePath))
                    {
                        try 
                        { 
                            Directory.Delete(workspacePath, true); 
                        } 
                        catch (Exception ex)
                        { 
                            Console.Error.WriteLine($"[DEBUG] Failed to delete workspace directory for {taskId}: {ex.Message}");
                        }
                    }
                }
            }

            // Clean up session locks and state semaphores to prevent handle leaks
            foreach (var taskId in req.TaskIds)
            {
                taskStateManager.CleanupSession(taskId);
                lockManager.CleanupSession(taskId);
            }

            return Results.Ok(new { deleted = req.TaskIds.Count });
        });

        // Get task events (log history)
        app.MapGet(ApiRoutes.TaskEvents, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            return Results.Ok(await db.TaskEvents
                .Where(e => e.SessionId == taskId)
                .OrderBy(e => e.Timestamp)
                .ToListAsync());
        });

        // Get chat history (for UI reconstruction)
        app.MapGet(ApiRoutes.TaskChat, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            return Results.Ok(await db.ChatMessages
                .Where(m => m.SessionId == taskId)
                .OrderBy(m => m.SequenceNumber)
                .Select(m => new { m.Role, m.Content, m.Timestamp, m.UserName, m.AgentName })
                .ToListAsync());
        });

        // Get implementation plan
        app.MapGet(ApiRoutes.TaskPlan, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            var plan = await db.ImplementationPlans
                .Where(p => p.SessionId == taskId)
                .OrderByDescending(p => p.Version)
                .FirstOrDefaultAsync();
            return plan != null ? Results.Ok(plan) : Results.NotFound();
        });

        // Get all plan versions
        app.MapGet(ApiRoutes.TaskPlans, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            return Results.Ok(await db.ImplementationPlans
                .Where(p => p.SessionId == taskId)
                .OrderBy(p => p.Version)
                .ToListAsync());
        });

        // Legacy: Get task logs (projected from events)
        app.MapGet(ApiRoutes.TaskLogs, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb, DateTime? before, int limit = 200) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            var query = db.TaskEvents.Where(e => e.SessionId == taskId);

            if (before.HasValue)
            {
                query = query.Where(e => e.Timestamp < before.Value);
            }

            var items = await query
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)
                .Select(e => new AgentTaskLog
                {
                    Id = e.Id,
                    TaskId = e.SessionId,
                    Timestamp = e.Timestamp,
                    LogLevel = e.Level,
                    Message = e.Payload
                })
                .ToListAsync();
                
            items.Reverse(); // Return in chronological order
            return Results.Ok(items);
        });

        // Pause a task
        app.MapPost(ApiRoutes.TaskPause, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb, TaskOperationsService taskOps, AgentTaskStore store) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            var taskSession = authResult.Task!;
            if (taskSession.State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled)
            {
                return Results.BadRequest("Cannot pause a task in a terminal state.");
            }

            if (taskSession.State is TaskState.AwaitingApproval or TaskState.PlanReview or TaskState.AwaitingInput ||
                taskOps.IsAwaitingApproval(taskId) || taskOps.IsAwaitingPlanDecision(taskId))
            {
                return Results.BadRequest("Cannot pause a task that is currently awaiting user approval, plan review, or input.");
            }

            taskOps.PauseTask(taskId);
            await store.UpdateStateAsync(taskId, TaskState.Paused, "Paused by user");
            return Results.Ok();
        });

        app.MapPost(ApiRoutes.TaskResume, async (Guid taskId, HttpContext context, TaskOperationsService taskOps, AgentTaskStore store, AppDbContext db, AuthDbContext authDb, AgentTaskQueueService queue) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            var taskSession = authResult.Task!;

            // Terminal states cannot be resumed - user must restart or proceed
            if (taskSession.State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled)
            {
                return Results.BadRequest($"Cannot resume task in terminal state '{taskSession.State}'. Use restart or proceed instead.");
            }

            // After app restart, the in-memory execution state is gone.
            // ResumeTask signals an in-memory pause gate. If the task is active in memory,
            // has an active pause gate, or is currently queued/in-flight, resume it directly.
            // Only if the task has NO in-memory presence whatsoever is it treated as an orphan restart.
            if (taskOps.IsTaskActive(taskId) || await taskOps.IsPausedAsync(taskId, CancellationToken.None) || queue.IsTaskInFlight(taskId))
            {
                // Normal resume - task is still managed in memory
                taskOps.ResumeTask(taskId);
                TaskState newState;
                if (taskOps.IsAwaitingApproval(taskId) || taskSession.State == TaskState.AwaitingApproval)
                {
                    newState = TaskState.AwaitingApproval;
                }
                else if (taskOps.IsAwaitingPlanDecision(taskId) || taskSession.State == TaskState.PlanReview)
                {
                    newState = TaskState.PlanReview;
                }
                else
                {
                    newState = queue.IsTaskQueued(taskId) ? TaskState.Queued : TaskState.InProgress;
                }
                await store.UpdateStateAsync(taskId, newState, "Resumed by user");
            }
            else
            {
                // Orphan resume - task has no execution loop (app was restarted)
                var session = taskSession;

                var userSession = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
                if (userSession != null && userSession.Role == "User")
                {
                    if (!string.IsNullOrEmpty(session.SourcePluginId) && !string.IsNullOrEmpty(session.PluginTaskId))
                    {
                        var allPerms = await authDb.GetEffectivePermissionsAsync(userSession.UserId);
                        if (!allPerms.HasPermission(session.SourcePluginId, session.PluginTaskId))
                            return Results.Forbid();

                        if (session.Mode == ExecutionMode.Auto && !allPerms.HasPermission(session.SourcePluginId, session.PluginTaskId, "AutoExecution"))
                            return Results.BadRequest("You do not have permission for AutoExecution on this task.");
                    }
                }

                await queue.EnqueueTaskAsync(new AgentTaskRequest
                {
                    TaskId = taskId,
                    AgentName = session.AgentName,
                    Prompt = session.UserPrompt,
                    Mode = session.Mode,
                    DebugMode = session.DebugMode,
                    TraceMode = session.TraceMode,
                    MaxSteps = session.MaxSteps,
                    UserName = userSession?.Username,
                    IsContinuation = true,
                    PluginTaskId = session.PluginTaskId,
                    SourcePluginId = session.SourcePluginId
                });
                await store.UpdateStateAsync(taskId, TaskState.Draft, "Re-enqueued after app restart");
            }
            return Results.Ok();
        });

        app.MapDelete(ApiRoutes.TaskById, async (Guid taskId, HttpContext context, AuthDbContext authDb, TaskOperationsService taskOps, AppDbContext db, AgentTaskStore store, AgentSystemOptions sysOptions, SemanticAgentBroker.Core.Features.TaskState.ITaskStateManager taskStateManager, SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager lockManager, AgentTaskQueueService queue) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb, checkDelete: true);
            if (authResult.Error != null) return authResult.Error;

            queue.CancelQueuedTask(taskId);
            taskOps.CancelTask(taskId);
            var stoppedOps = await taskOps.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5));
            var stoppedQueue = await queue.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5));
            if (!stoppedOps || !stoppedQueue)
                return Results.Conflict("Task is active and could not be stopped within the timeout. Please try again.");

            // Use UpdateStateAsync only (it handles both DB save and SignalR broadcast).
            await store.UpdateStateAsync(taskId, TaskState.Cancelled, "Deleted by user");
            
            if (sysOptions.CleanupWorkspacesOnTaskDelete)
            {
                var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
                if (Directory.Exists(workspacePath))
                {
                    try 
                    { 
                        Directory.Delete(workspacePath, true); 
                    } 
                    catch (Exception ex)
                    { 
                        Console.Error.WriteLine($"[DEBUG] Failed to delete workspace directory for {taskId}: {ex.Message}");
                    }
                }
            }

            taskStateManager.CleanupSession(taskId);
            lockManager.CleanupSession(taskId);

            return Results.Ok();
        });

        app.MapPost(ApiRoutes.TaskRestart, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb, AgentTaskQueueService queue, TaskOperationsService taskOps, AgentTaskStore store, AgentSystemOptions sysOptions, SemanticAgentBroker.Core.Features.TaskState.ITaskStateManager taskStateManager, SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager lockManager) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            // Re-fetch as tracked entity so property changes are persisted by SaveChangesAsync
            var session = await db.TaskSessions.FindAsync(taskId);
            if (session == null) return Results.NotFound();

            if (session.State is TaskState.InProgress or TaskState.AwaitingApproval or TaskState.AwaitingInput or TaskState.Planning or TaskState.PlanReview or TaskState.Queued)
                return Results.BadRequest("Task is still active. Cancel it first.");

            // If task was paused while active in memory or in flight, cleanly cancel and await completion of the previous execution loop
            if (taskOps.IsTaskActive(taskId) || queue.IsTaskInFlight(taskId))
            {
                queue.CancelQueuedTask(taskId);
                taskOps.CancelTask(taskId);
                var stoppedOps = await taskOps.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5));
                var stoppedQueue = await queue.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5));
                if (!stoppedOps || !stoppedQueue)
                    return Results.Conflict("Previous execution loop did not stop within the timeout. Please try again.");
            }

            var userSession = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            if (userSession != null && userSession.Role == "User")
            {
                if (!string.IsNullOrEmpty(session.SourcePluginId) && !string.IsNullOrEmpty(session.PluginTaskId))
                {
                    var allPerms = await authDb.GetEffectivePermissionsAsync(userSession.UserId);
                    if (!allPerms.HasPermission(session.SourcePluginId, session.PluginTaskId))
                        return Results.Forbid();

                    if (session.Mode == ExecutionMode.Auto && !allPerms.HasPermission(session.SourcePluginId, session.PluginTaskId, "AutoExecution"))
                        return Results.BadRequest("You do not have permission for AutoExecution on this task.");
                }
            }

            await db.TaskEvents.Where(e => e.SessionId == taskId).ExecuteDeleteAsync();
            await db.ChatMessages.Where(m => m.SessionId == taskId).ExecuteDeleteAsync();
            await db.ImplementationPlans.Where(p => p.SessionId == taskId).ExecuteDeleteAsync();

            session.State = TaskState.Draft;
            session.StartedAt = null;
            session.CompletedAt = null;
            session.FinalResult = null;

            if (!string.IsNullOrEmpty(session.UserPrompt))
            {
                db.ChatMessages.Add(new ChatMessageSnapshot
                {
                    SessionId = taskId,
                    SequenceNumber = 0,
                    Role = "user",
                    Content = session.UserPrompt,
                    UserName = userSession?.Username,
                    Timestamp = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync();

            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);
            if (Directory.Exists(workspacePath))
            {
                try
                {
                    Directory.Delete(workspacePath, true);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DEBUG] Failed to clean workspace directory for {taskId}: {ex.Message}");
                }
            }
            taskStateManager.CleanupSession(taskId);
            lockManager.CleanupSession(taskId);

            var req = new AgentTaskRequest
            {
                TaskId = taskId,
                AgentName = session.AgentName,
                Prompt = session.UserPrompt,
                Mode = session.Mode,
                DebugMode = session.DebugMode,
                TraceMode = session.TraceMode,
                MaxSteps = session.MaxSteps,
                UserName = userSession?.Username,
                PluginTaskId = session.PluginTaskId,
                SourcePluginId = session.SourcePluginId
            };
            await queue.EnqueueTaskAsync(req);

            await store.UpdateStateAsync(taskId, TaskState.Draft, "Task restarted by user");
            return Results.Ok(req);
        });

        app.MapPost(ApiRoutes.TaskProceed, async (Guid taskId, [FromBody] string prompt, HttpContext context, AppDbContext db, AuthDbContext authDb, AgentTaskQueueService queue, AgentTaskStore store, AgentSystemOptions sysOptions, TaskOperationsService taskOps) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            // Re-fetch as tracked entity so property changes are persisted by SaveChangesAsync
            var session = await db.TaskSessions.FindAsync(taskId);
            if (session == null) return Results.NotFound();

            if (session.State is TaskState.InProgress or TaskState.AwaitingApproval or TaskState.AwaitingInput or TaskState.Planning or TaskState.PlanReview or TaskState.Queued)
                return Results.BadRequest("Task is still active. Pause or Cancel it first.");

            // If task was paused while active in memory or in flight, cleanly cancel and await completion of the previous execution loop
            if (taskOps.IsTaskActive(taskId) || queue.IsTaskInFlight(taskId))
            {
                queue.CancelQueuedTask(taskId);
                taskOps.CancelTask(taskId);
                var stoppedOps = await taskOps.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5));
                var stoppedQueue = await queue.WaitForTaskCompletionAsync(taskId, TimeSpan.FromSeconds(5));
                if (!stoppedOps || !stoppedQueue)
                    return Results.Conflict("Previous execution loop did not stop within the timeout. Please try again.");
            }

            var userSession = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            if (userSession != null && userSession.Role == "User")
            {
                if (!string.IsNullOrEmpty(session.SourcePluginId) && !string.IsNullOrEmpty(session.PluginTaskId))
                {
                    var allPerms = await authDb.GetEffectivePermissionsAsync(userSession.UserId);
                    if (!allPerms.HasPermission(session.SourcePluginId, session.PluginTaskId))
                        return Results.Forbid();

                    if (session.Mode == ExecutionMode.Auto && !allPerms.HasPermission(session.SourcePluginId, session.PluginTaskId, "AutoExecution"))
                        return Results.BadRequest("You do not have permission for AutoExecution on this task.");
                }
            }

            session.State = TaskState.Draft;
            session.CompletedAt = null;
            session.FinalResult = null;

            var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(taskId, sysOptions);

            // Archive the old plan
            var planPath = Path.Combine(workspacePath, "_implementation_plan.md");
            if (File.Exists(planPath))
            {
                var archiveName = $"_plan_v{DateTime.UtcNow:yyyyMMdd_HHmmss}.md";
                File.Copy(planPath, Path.Combine(workspacePath, archiveName), overwrite: true);
                // Keep the plan file so BuildContextInjectionAsync has context
                // for the first step. The planning phase will overwrite it if
                // the continuation uses a planning mode.
                
                // Cleanup old archives based on config
                if (sysOptions.ArchivedPlansToKeep.HasValue)
                {
                    var archives = new DirectoryInfo(workspacePath)
                        .GetFiles("_plan_v*.md")
                        .OrderByDescending(f => f.Name)
                        .ToList();

                    foreach (var file in archives.Skip(sysOptions.ArchivedPlansToKeep.Value))
                    {
                        file.Delete();
                    }
                }
            }

            // Delete runtime plan so restart reverts to the approved baseline
            var runtimePlanPath = Path.Combine(workspacePath, "_runtime_plan.md");
            if (File.Exists(runtimePlanPath))
            {
                try { File.Delete(runtimePlanPath); } catch { }
            }


            var maxSeq = await db.ChatMessages.Where(m => m.SessionId == taskId).MaxAsync(m => (int?)m.SequenceNumber) ?? -1;

            db.ChatMessages.Add(new ChatMessageSnapshot 
            { 
                SessionId = taskId, 
                SequenceNumber = maxSeq + 1, 
                Role = "user", 
                Content = prompt, 
                UserName = userSession?.Username,
                Timestamp = DateTime.UtcNow 
            });

            await db.SaveChangesAsync();

            var req = new AgentTaskRequest
            {
                TaskId = taskId,
                AgentName = session.AgentName,
                Prompt = prompt, // ONLY the clean user prompt
                Mode = session.Mode,
                DebugMode = session.DebugMode,
                TraceMode = session.TraceMode,
                MaxSteps = session.MaxSteps,
                UserName = userSession?.Username,
                IsContinuation = true,
                PluginTaskId = session.PluginTaskId,
                SourcePluginId = session.SourcePluginId
            };
            await queue.EnqueueTaskAsync(req);

            await store.UpdateStateAsync(taskId, TaskState.Draft, "Task continuation requested");
            return Results.Ok(req);
        });

        // Plan review actions via REST
        app.MapPost(ApiRoutes.TaskPlanApprove, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb, TaskOperationsService taskOps) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            taskOps.ApprovePlan(taskId);
            return Results.Ok();
        });

        app.MapPost(ApiRoutes.TaskPlanModify, async (Guid taskId, [FromBody] string modifiedPlan, HttpContext context, AppDbContext db, AuthDbContext authDb, TaskOperationsService taskOps) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            taskOps.ModifyPlan(taskId, modifiedPlan);
            return Results.Ok();
        });

        app.MapPost(ApiRoutes.TaskPlanReject, async (Guid taskId, [FromBody] string feedback, HttpContext context, AppDbContext db, AuthDbContext authDb, TaskOperationsService taskOps) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            taskOps.RejectPlan(taskId, feedback);
            return Results.Ok();
        });

        app.MapPost(ApiRoutes.TaskPlanQuestion, async (Guid taskId, [FromBody] string question, HttpContext context, AppDbContext db, AuthDbContext authDb, TaskOperationsService taskOps) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            taskOps.AskPlanQuestion(taskId, question);
            return Results.Ok();
        });

        app.MapPost(ApiRoutes.TaskSendInterruption, async (Guid taskId, [FromBody] string message, HttpContext context, AppDbContext db, AuthDbContext authDb, TaskOperationsService taskOps) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            bool delivered = taskOps.SendInterruption(taskId, message, session?.Username ?? "User");
            if (!delivered)
            {
                return Results.BadRequest(new { error = "Task is not currently running or active to receive interruptions." });
            }
            return Results.Ok(new { success = true });
        });

        app.MapPost(ApiRoutes.TaskApproveStep, async (Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb, TaskOperationsService taskOps) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            taskOps.ApproveStep(taskId);
            return Results.Ok();
        });

        app.MapPost(ApiRoutes.TaskRejectStep, async (Guid taskId, [FromBody] string feedback, HttpContext context, AppDbContext db, AuthDbContext authDb, TaskOperationsService taskOps) =>
        {
            var authResult = await AuthorizeTaskOperationAsync(taskId, context, db, authDb);
            if (authResult.Error != null) return authResult.Error;

            var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
            taskOps.RejectStep(taskId, feedback, session?.Username ?? "User");
            return Results.Ok();
        });
    }

    private static async Task<(IResult? Error, TaskSession? Task)> AuthorizeTaskOperationAsync(
        Guid taskId, HttpContext context, AppDbContext db, AuthDbContext authDb, bool checkDelete = false)
    {
        var session = context.Items["SessionInfo"] as SemanticAgentBroker.Api.Features.Auth.SessionInfo;
        if (session == null) return (Results.Unauthorized(), null);

        var task = await db.TaskSessions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId);
        if (task == null) return (Results.NotFound(), null);

        if (session.Role == "Admin") return (null, task);
        if (task.CreatedByUserId == session.UserId) return (null, task);

        // Non-owner cannot access private tasks, even with matching permissions
        if (task.IsPrivate)
            return (Results.Forbid(), null);

        if (task.SourcePluginId != null && task.PluginTaskId != null)
        {
            var perms = await authDb.GetEffectivePermissionsAsync(session.UserId);
            if (perms.HasPermission(task.SourcePluginId, task.PluginTaskId))
            {
                if (checkDelete && !perms.HasPermission(task.SourcePluginId, task.PluginTaskId, "Delete"))
                    return (Results.Forbid(), null);
                
                return (null, task);
            }
        }

        return (Results.Forbid(), null);
    }
}
