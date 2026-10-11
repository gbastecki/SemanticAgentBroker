using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Api.Features.ReAct.Services;
using SemanticAgentBroker.Core.Builders;
using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Core.Features.Agents;
using SemanticAgentBroker.Core.Features.PluginHost;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Core.LoadBalancing;
using SemanticAgentBroker.Core.Services;
using SemanticAgentBroker.Sdk;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using System.Threading.Channels;

namespace SemanticAgentBroker.Api.Features.Tasks.Services;

public class AgentTaskQueueService : BackgroundService
{
    private readonly Channel<AgentTaskRequest> _queue;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _queuedTasks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> _activeTasks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid> _latestRequestIds = new();
    private readonly ILogger<AgentTaskQueueService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly AiProviderOptions _providerOptions;
    private readonly AgentSystemOptions _systemOptions;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly IConnectionLoadBalancer _loadBalancer;
    private readonly SemaphoreSlim _executionSemaphore;

    public AgentTaskQueueService(
        IServiceProvider serviceProvider,
        ILogger<AgentTaskQueueService> logger,
        AiProviderOptions providerOptions,
        AgentSystemOptions systemOptions,
        IAgentRegistry agentRegistry,
        IPluginRegistry pluginRegistry,
        IConnectionLoadBalancer loadBalancer)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _providerOptions = providerOptions;
        _systemOptions = systemOptions;
        _agentRegistry = agentRegistry;
        _pluginRegistry = pluginRegistry;
        _loadBalancer = loadBalancer;
        _queue = Channel.CreateUnbounded<AgentTaskRequest>();
        var maxConcurrent = Math.Max(1, systemOptions.MaxConcurrentAgents);
        _executionSemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public async ValueTask EnqueueTaskAsync(AgentTaskRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            request.RequestId = Guid.NewGuid();
        }
        _latestRequestIds[request.TaskId] = request.RequestId;
        _queuedTasks.TryAdd(request.TaskId, 0);
        await _queue.Writer.WriteAsync(request);
    }

    public void CancelQueuedTask(Guid taskId)
    {
        _latestRequestIds.TryRemove(taskId, out _);
        _queuedTasks.TryRemove(taskId, out _);
    }

    public bool IsTaskQueued(Guid taskId) => _queuedTasks.ContainsKey(taskId);
    public bool IsTaskExecuting(Guid taskId) => _activeTasks.ContainsKey(taskId);
    public bool IsTaskInFlight(Guid taskId) => _queuedTasks.ContainsKey(taskId) || _activeTasks.ContainsKey(taskId);

    /// <summary>
    /// Waits asynchronously for an active in-flight task to finish execution or cancellation.
    /// </summary>
    public async Task<bool> WaitForTaskCompletionAsync(Guid taskId, TimeSpan timeout)
    {
        if (_activeTasks.TryGetValue(taskId, out var task))
        {
            var delayTask = Task.Delay(timeout);
            var completed = await Task.WhenAny(task, delayTask);
            return completed == task;
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                // Check if this queued request was superseded by a newer request or cancelled while queued
                if (!_latestRequestIds.TryGetValue(request.TaskId, out var latestReqId) || latestReqId != request.RequestId)
                {
                    _logger.LogInformation("Queued task {TaskId} request {RequestId} was superseded or cancelled. Skipping request.",
                        request.TaskId, request.RequestId);
                    _queuedTasks.TryRemove(request.TaskId, out _);
                    continue;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_activeTasks.TryAdd(request.TaskId, tcs.Task))
                {
                    _logger.LogWarning("Task {TaskId} is already active. Skipping duplicate execution.", request.TaskId);
                    _queuedTasks.TryRemove(request.TaskId, out _);
                    continue;
                }
                _queuedTasks.TryRemove(request.TaskId, out _);

                // Register task in TaskOperationsService immediately to eliminate any race window
                // where concurrent API operations (Delete, Restart, Proceed) might perceive the task as inactive.
                var taskOps = _serviceProvider.GetRequiredService<TaskOperationsService>();
                var taskCt = taskOps.RegisterTask(request.TaskId);

                try
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RunTaskAsync(request, taskCt, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Critical failure caught traversing Agent Dispatcher Loop for task {TaskId}.", request.TaskId);
                            try
                            {
                                // Fallback state update in case fundamental DI resolution or DB fetch fails before execution loop starts
                                using var scope = _serviceProvider.CreateScope();
                                var taskStore = scope.ServiceProvider.GetRequiredService<AgentTaskStore>();
                                await taskStore.UpdateStateAsync(request.TaskId, TaskState.Failed, "Task dispatch failed critically: " + ex.Message);
                            }
                            catch (Exception fallbackEx)
                            {
                                _logger.LogDebug(fallbackEx, "Fallback state update failed for task {TaskId}.", request.TaskId);
                            }
                        }
                        finally
                        {
                            _activeTasks.TryRemove(request.TaskId, out _);
                            tcs.TrySetResult();
                        }
                    });
                }
                catch (Exception ex)
                {
                    taskOps.UnregisterTask(request.TaskId);
                    _activeTasks.TryRemove(request.TaskId, out _);
                    tcs.TrySetResult();
                    _logger.LogError(ex, "Failed to schedule worker task execution for {TaskId}.", request.TaskId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit on stoppingToken cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error on worker channel enumerator.");
        }

        // Host shutdown blocks precisely here until ongoing agent tool cycles break loop on CancellationToken.
        try
        {
            var shutdownTimeout = TimeSpan.FromSeconds(10);
            await Task.WhenAll(_activeTasks.Values).WaitAsync(shutdownTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Host shutdown timed out waiting for {Count} active agent task(s) to finish.", _activeTasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while awaiting active agent tasks during host shutdown.");
        }
    }

    private async Task RunTaskAsync(AgentTaskRequest request, CancellationToken taskCt, CancellationToken hostToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var taskOps = scope.ServiceProvider.GetRequiredService<TaskOperationsService>();

        // Safety: ensure loop doesn't run infinitely, providing a maximum global timeout (default 6 hours)
        var globalTimeout = _systemOptions.GlobalTaskTimeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(_systemOptions.GlobalTaskTimeoutSeconds.Value)
            : (_systemOptions.GlobalTaskTimeoutMinutes > 0
                ? TimeSpan.FromMinutes(_systemOptions.GlobalTaskTimeoutMinutes)
                : TimeSpan.FromHours(6));
        using var globalTimeoutCts = new CancellationTokenSource(globalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken, taskCt, globalTimeoutCts.Token);
        var ct = linkedCts.Token;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var taskStore = scope.ServiceProvider.GetRequiredService<AgentTaskStore>();
        var engine = scope.ServiceProvider.GetRequiredService<IReactExecutionEngine>();
        var context = scope.ServiceProvider.GetRequiredService<IAgentTaskContext>();
        string? connectionId = null;
        bool semaphoreHeld = false;
        SemaphoreOwnershipState? semState = null;

        try
        {
            // Fetch session without tracking to prevent memory footprint bloat and concurrency exceptions
            // during long-running AI execution loops. State updates will be handled explicitly via taskStore.
            var session = await db.TaskSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.TaskId, ct);
            if (session == null) return;

            // If request was superseded or cancelled while waiting in queue, do not execute it
            if (!_latestRequestIds.TryGetValue(request.TaskId, out var activeReqId) || activeReqId != request.RequestId)
            {
                _logger.LogInformation("Task {TaskId} request {RequestId} superseded or cancelled before execution start. Skipping.", request.TaskId, request.RequestId);
                return;
            }

            // If task was cancelled or terminated while in the queue, do not execute it
            if (session.State is TaskState.Cancelled or TaskState.Completed or TaskState.Failed)
                return;

            context.Initialize(request.TaskId);

            // If task was paused while waiting in queue, wait for user resume before occupying an execution slot
            var isPaused = await taskOps.IsPausedAsync(request.TaskId, ct);
            if (!isPaused && session.State == TaskState.Paused)
            {
                // Verify fresh DB state to avoid TOCTOU race
                var currentState = await db.TaskSessions
                    .AsNoTracking()
                    .Where(s => s.Id == request.TaskId)
                    .Select(s => s.State)
                    .FirstOrDefaultAsync(ct);

                if (currentState == TaskState.Paused)
                {
                    taskOps.PauseTask(request.TaskId);
                    isPaused = true;
                }
            }

            if (isPaused)
            {
                await taskStore.UpdateStateAsync(request.TaskId, TaskState.Paused, "Task is paused by user.");
                await taskStore.BroadcastActivityAsync(request.TaskId, "Task is paused.");
                await taskOps.WaitIfPausedAsync(request.TaskId, ct);
                await taskStore.UpdateStateAsync(request.TaskId, TaskState.Queued, "Task resumed. Waiting for execution slot...");
                await taskStore.BroadcastActivityAsync(request.TaskId, "Waiting for execution slot...");
            }

            // Acquire execution semaphore - blocks until a concurrency slot opens
            await _executionSemaphore.WaitAsync(ct);
            semaphoreHeld = true;
            semState = new SemaphoreOwnershipState { IsHeld = true };

            // Check if task was paused while waiting for semaphore slot (use while loop to handle re-pauses while waiting for slot)
            while (await taskOps.IsPausedAsync(request.TaskId, ct))
            {
                bool semaphoreReleased = false;
                if (semState.TryRelease())
                {
                    try
                    {
                        _executionSemaphore.Release();
                        semaphoreReleased = true;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Host shutdown may have already disposed the semaphore
                    }
                }
                bool pauseResumeCompleted = false;
                try
                {
                    await taskStore.UpdateStateAsync(request.TaskId, TaskState.Paused, "Task is paused by user.");
                    await taskStore.BroadcastActivityAsync(request.TaskId, "Task is paused.");
                    await taskOps.WaitIfPausedAsync(request.TaskId, ct);
                    await taskStore.UpdateStateAsync(request.TaskId, TaskState.Queued, "Task resumed. Waiting for execution slot...");
                    await taskStore.BroadcastActivityAsync(request.TaskId, "Waiting for execution slot...");
                    pauseResumeCompleted = true;
                }
                finally
                {
                    if (semaphoreReleased)
                    {
                        try
                        {
                            await _executionSemaphore.WaitAsync(ct);
                            semState.TryAcquire();
                        }
                        catch (OperationCanceledException)
                        {
                            semState.IsHeld = false;
                            if (pauseResumeCompleted)
                            {
                                ct.ThrowIfCancellationRequested();
                            }
                        }
                        catch (ObjectDisposedException ex)
                        {
                            semState.IsHeld = false;
                            throw new OperationCanceledException("Execution semaphore disposed during host shutdown.", ex, ct);
                        }
                    }
                }
            }

            // If request was superseded or cancelled while paused/waiting for semaphore, do not proceed
            if (!_latestRequestIds.TryGetValue(request.TaskId, out var latestReqAfterPause) || latestReqAfterPause != request.RequestId)
            {
                _logger.LogInformation("Task {TaskId} request {RequestId} superseded or cancelled while waiting for resume/slot. Skipping.", request.TaskId, request.RequestId);
                return;
            }

            await taskStore.UpdateStateAsync(request.TaskId, TaskState.InProgress, "Task started");

            // Initialize trace logging context for this async task flow
            SemanticAgentBroker.Core.Infrastructure.Http.TraceLoggingContext.IsTraceEnabled = request.TraceMode;
            SemanticAgentBroker.Core.Infrastructure.Http.TraceLoggingContext.CurrentTaskId = request.TaskId;

            // ── Build Kernel & System Prompt ───────────────────────────────
            var (kernel, systemPrompt, connId) = await BuildAgentKernelAsync(scope.ServiceProvider, request, db, ct);
            connectionId = connId;

            if (request.DebugMode && connectionId != null)
            {
                var conn = _providerOptions.Connections.FirstOrDefault(c => c.Id == connectionId);
                await taskStore.LogEventAsync(request.TaskId, EventTypes.Debug, LogLevelNames.System,
                    $"[DEBUG] Load balancer selected endpoint: '{conn?.EndpointName ?? connectionId}' ({connectionId})");
            }

            // ── Planning Phase ────────────────────────────────────────────
            {
                var planningEngine = scope.ServiceProvider.GetRequiredService<PlanningEngine>();
                bool autoApprove = request.Mode == ExecutionMode.Auto;

                // Inject current state for continuations so planning isn't blind
                string planningContext = request.Prompt;
                if (request.IsContinuation)
                {
                    var sysOptions = scope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Shared.Configuration.AgentSystemOptions>();
                    var statePath = Path.Combine(SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(request.TaskId, sysOptions), "_task_state.md");
                    if (File.Exists(statePath))
                    {
                        planningContext = $"[CURRENT STATE]\n{await File.ReadAllTextAsync(statePath)}\n\n[FOLLOW-UP REQUEST]\n{request.Prompt}";
                    }
                }

                var approvedPlan = await planningEngine.GeneratePlanAsync(
                    request.TaskId, kernel, systemPrompt, planningContext,
                    request.DebugMode, autoApprove, _executionSemaphore, semState!, ct);

                if (approvedPlan == null)
                {
                    await taskStore.UpdateStateAsync(request.TaskId, TaskState.Failed,
                        "Planning phase did not produce an approved plan.");
                    return;
                }

                systemPrompt += $"\n\n## Approved Implementation Plan\n" +
                    $"This is the approved baseline plan for this task. Follow it step by step unless updated by an Active Runtime Plan in your task state:\n\n{approvedPlan}";
            }

            int? targetMaxSteps = request.MaxSteps;

            // ── Delegate to ReAct Engine ───────────────────────────────────
            var finalResult = await engine.ExecuteAsync(
                request.TaskId, kernel, systemPrompt,
                originalObjective: request.Prompt,
                debugMode: request.DebugMode,
                isContinuation: request.IsContinuation, maxSteps: targetMaxSteps,
                executionSemaphore: _executionSemaphore, semaphoreState: semState, ct: ct);

            // ── Persist final result ───────────────────────────────────────
            await taskStore.SetFinalResultAsync(request.TaskId, finalResult);

            // Ensure final result is also recorded in the chat snapshots if not already captured
            try
            {
                var history = await taskStore.GetChatHistoryAsync(request.TaskId);
                bool hasFinalInChat = history.Any(m => m.Role == "assistant" &&
                    (m.Content.Contains(finalResult) || (m.Content.Length > 20 && finalResult.Contains(m.Content[..Math.Min(80, m.Content.Length)]))));
                if (!hasFinalInChat && !string.IsNullOrWhiteSpace(finalResult))
                {
                    var maxSeq = await taskStore.GetMaxSequenceNumberAsync(request.TaskId);
                    await taskStore.SaveChatMessageAsync(request.TaskId, maxSeq + 1, "assistant", $"{ReactFormatTokens.FinalAnswer} {finalResult}", "Orchestrator");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist final result snapshot to chat history for task {TaskId}.", request.TaskId);
            }

            if (finalResult.StartsWith("Task failed:", StringComparison.OrdinalIgnoreCase) || finalResult.Contains("Failed to reach a valid FinalAnswer"))
            {
                await taskStore.UpdateStateAsync(request.TaskId, TaskState.Failed, finalResult);
                await taskStore.LogEventAsync(request.TaskId, EventTypes.TaskCompleted, LogLevelNames.System, $"Final Result: {finalResult} (Marked as Failed)");
            }
            else
            {
                await taskStore.UpdateStateAsync(request.TaskId, TaskState.Completed, finalResult);
                await taskStore.LogEventAsync(request.TaskId, EventTypes.TaskCompleted, LogLevelNames.System, $"Final Result: {finalResult}");
            }
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Task {TaskId} failed due to HTTP timeout.", request.TaskId);
            await taskStore.UpdateStateAsync(request.TaskId, TaskState.Failed,
                "HTTP request timed out while waiting for a response from the LLM. Try again.");
        }
        catch (OperationCanceledException ex) when (ex.InnerException is TimeoutException || ex.InnerException is TaskCanceledException)
        {
            _logger.LogError(ex, "Task {TaskId} failed due to LLM timeout.", request.TaskId);
            await taskStore.UpdateStateAsync(request.TaskId, TaskState.Failed,
                "LLM request timed out or was cancelled by system maximum timeout enforcement.");
        }
        catch (OperationCanceledException)
        {
            if (hostToken.IsCancellationRequested)
            {
                _logger.LogInformation("Task {TaskId} interrupted by system shutdown/restart.", request.TaskId);
                await taskStore.UpdateStateAsync(request.TaskId, TaskState.Paused, "Interrupted by system operations. Scheduled for recovery.");
            }
            else if (globalTimeoutCts.IsCancellationRequested)
            {
                _logger.LogError("Task {TaskId} exceeded the maximum allowed execution time.", request.TaskId);
                await taskStore.UpdateStateAsync(request.TaskId, TaskState.Failed, "Task exceeded the maximum allowed execution time.");
            }
            else
            {
                _logger.LogInformation("Task {TaskId} was cancelled by user.", request.TaskId);
                await taskStore.UpdateStateAsync(request.TaskId, TaskState.Cancelled, "Task cancelled by user.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent execution failed for task {TaskId}.", request.TaskId);
            string errorMsg = ex.InnerException != null ? $"{ex.Message} -> {ex.InnerException.Message}" : ex.Message;
            await taskStore.UpdateStateAsync(request.TaskId, TaskState.Failed, $"Task dispatch failed critically: {errorMsg}");
        }
        finally
        {
            SemanticAgentBroker.Core.Infrastructure.Http.TraceLoggingContext.IsTraceEnabled = false;
            SemanticAgentBroker.Core.Infrastructure.Http.TraceLoggingContext.CurrentTaskId = null;

            // Release load balancer connection
            if (connectionId != null)
                _loadBalancer.ReleaseConnection(connectionId);

            if (semaphoreHeld && (semState?.TryRelease() ?? true))
            {
                try
                {
                    _executionSemaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Host shutdown may have already disposed the semaphore
                }
            }

            taskOps.UnregisterTask(request.TaskId);

            if (_latestRequestIds.TryGetValue(request.TaskId, out var currentId) && currentId == request.RequestId)
            {
                _latestRequestIds.TryRemove(request.TaskId, out _);
            }

            try
            {
                using var cleanupScope = _serviceProvider.CreateScope();
                var stateManager = cleanupScope.ServiceProvider.GetRequiredService<ITaskStateManager>();
                stateManager.CleanupSession(request.TaskId);

                var lockManager = cleanupScope.ServiceProvider.GetRequiredService<SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager>();
                lockManager.CleanupSession(request.TaskId);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogDebug(cleanupEx, "Failed to cleanup session state/locks for task {TaskId}.", request.TaskId);
            }
        }
    }

    // ── Agent Building (Dynamic Plugin-Based, Load-Balanced) ───────────────

    private async Task<(Kernel Kernel, string SystemPrompt, string? ConnectionId)> BuildAgentKernelAsync(
        IServiceProvider scopedProvider, AgentTaskRequest request, AppDbContext db, CancellationToken ct)
    {
        AgentDefinition? agentDef = null;
        if (string.IsNullOrEmpty(request.PluginTaskId))
        {
            agentDef = _agentRegistry.Resolve(request.AgentName);
        }

        var modelRoute = _providerOptions.ModelRouting.GetValueOrDefault(TaskModelRole.PrimaryTextModel);
        if (modelRoute == null || string.IsNullOrEmpty(modelRoute.ModelId))
        {
            throw new InvalidOperationException("Failed to resolve PrimaryTextModel from AiProviders configuration in appsettings.json.");
        }

        var kernelProvider = scopedProvider.GetRequiredService<SemanticAgentBroker.Core.Features.KernelProvider.IKernelProviderService>();
        IKernelBuilder kernelBuilder;
        string? connectionId = null;

        try
        {
            if (!string.IsNullOrEmpty(modelRoute.ProviderId))
            {
                kernelBuilder = kernelProvider.CreateKernelBuilder(modelRoute.ProviderId, modelRoute.ModelId);
                _logger.LogDebug("[AgentQueue] Using sticky provider '{ProviderId}' for model '{ModelId}'",
                    modelRoute.ProviderId, modelRoute.ModelId);
            }
            else
            {
                var (builder, connId) = await kernelProvider.CreateKernelBuilderBalancedAsync(modelRoute.ModelId, ct);
                kernelBuilder = builder;
                connectionId = connId;
                _logger.LogDebug("[AgentQueue] Load balancer selected connection '{ConnectionId}' for model '{ModelId}'",
                    connId, modelRoute.ModelId);
            }

            kernelBuilder.Services.AddSingleton(scopedProvider);
            foreach (var filter in scopedProvider.GetServices<IFunctionInvocationFilter>())
                kernelBuilder.Services.AddSingleton(filter);
            foreach (var filter in scopedProvider.GetServices<IPromptRenderFilter>())
                kernelBuilder.Services.AddSingleton(filter);

            var kernel = kernelBuilder.Build();

            // ── Register dynamic plugins from agent's method bindings ──────
            var taskContext = scopedProvider.GetRequiredService<IAgentTaskContext>();
            var contextFactory = scopedProvider.GetRequiredService<IPluginContextFactory>();

            var bindingsToUse = new List<(string PluginId, string MethodId)>();
            var safetyOverrides = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            string systemInstruction = string.Empty;
            string? taskObjective = request.Prompt;

            // 1. AUTO-INJECT Core Tools (BuiltInRequired plugins)
            foreach (var plugin in _pluginRegistry.GetAllPlugins().Where(p => p.Plugin.Category == PluginCategory.BuiltInRequired))
            {
                foreach (var method in plugin.Plugin.GetMethods())
                {
                    bindingsToUse.Add((plugin.Plugin.Id, method.Id));
                }
            }

            // 2. Resolve Task Specific bindings
            if (!string.IsNullOrEmpty(request.PluginTaskId) && !string.IsNullOrEmpty(request.SourcePluginId))
            {
                var sourcePlugin = _pluginRegistry.GetPlugin(request.SourcePluginId);
                if (sourcePlugin != null)
                {
                    var taskDef = sourcePlugin.Plugin.GetTasks().FirstOrDefault(t => t.Id == request.PluginTaskId);
                    if (taskDef != null)
                    {
                        systemInstruction = taskDef.SystemInstruction ?? "You are an intelligent agent executing a specific task.";
                        taskObjective = $"Task Name: {taskDef.Name}\nDescription: {taskDef.Description}\n\nUser Request: {request.Prompt}";
                        
                        foreach (var m in taskDef.AllowedMethods)
                        {
                            bindingsToUse.Add((request.SourcePluginId, m));
                        }
                        foreach (var ext in taskDef.ExternalMethods)
                        {
                            bindingsToUse.Add((ext.PluginId, ext.MethodId));
                            if (ext.IsDangerousOverride.HasValue)
                            {
                                safetyOverrides[$"{ext.PluginId}:{ext.MethodId}"] = ext.IsDangerousOverride.Value;
                            }
                        }
                        foreach (var over in taskDef.SafetyOverrides)
                        {
                            safetyOverrides[$"{request.SourcePluginId}:{over.MethodId}"] = over.IsDangerous;
                        }
                        
                        // Add required plugins from the source plugin
                        foreach (var req in sourcePlugin.Plugin.RequiredPlugins)
                        {
                            var reqPlugin = _pluginRegistry.GetPlugin(req);
                            if (reqPlugin != null)
                            {
                                foreach (var method in reqPlugin.Plugin.GetMethods())
                                    bindingsToUse.Add((reqPlugin.Plugin.Id, method.Id));
                            }
                        }
                    }
                }
            }
            else
            {
                systemInstruction = agentDef?.SystemInstruction ?? "You are an intelligent agent executing a specific task.";
                var agentBindings = request.RestrictedMethodIds != null && request.RestrictedMethodIds.Any()
                    ? agentDef?.MethodBindings.Where(b => request.RestrictedMethodIds.Contains(b.MethodId) || request.RestrictedMethodIds.Contains($"{b.PluginId}:{b.MethodId}")).ToList() ?? new List<AgentMethodBinding>()
                    : agentDef?.MethodBindings ?? new List<AgentMethodBinding>();
                
                foreach (var b in agentBindings) bindingsToUse.Add((b.PluginId, b.MethodId));
            }

            // Deduplicate bindings
            bindingsToUse = bindingsToUse.Distinct().ToList();
            var boundPluginIds = bindingsToUse.Select(b => b.PluginId).Distinct().ToList();

            // Load plugin DB configs for all bound plugins
            var pluginConfigs = new Dictionary<string, IReadOnlyDictionary<string, string>>();

            foreach (var pluginId in boundPluginIds)
            {
                var configValues = await db.PluginConfigValues
                    .Where(c => c.PluginId == pluginId)
                    .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

                if (configValues.Count > 0)
                    pluginConfigs[pluginId] = configValues;
            }

            var agentBuilder = new AgentBuilder(scopedProvider, agentDef?.Name ?? "RuntimeAgent")
                .WithSystemInstruction(systemInstruction)
                .WithTaskObjective(taskObjective)
                .WithRawMethodBindings(bindingsToUse)
                .WithSafetyOverrides(safetyOverrides);

            agentBuilder.RegisterPlugins(
                kernel, _pluginRegistry, contextFactory,
                taskContext.WorkspacePath, request.TaskId, pluginConfigs);

            int maxContextTokens = ModelRoutingHelper.ResolveMaxContextTokens(_providerOptions);
            var systemPrompt = agentBuilder.BuildSystemPrompt(kernel, maxContextTokens);

            if (request.DebugMode)
            {
                _logger.LogWarning("SYSTEM PROMPT GENERATED:\n{SystemPrompt}", systemPrompt);
            }

            return (kernel, systemPrompt, connectionId);
        }
        catch
        {
            if (connectionId != null)
                _loadBalancer.ReleaseConnection(connectionId);
            throw;
        }
    }
}
