using Microsoft.SemanticKernel;
using SemanticAgentBroker.Api.Features.Tasks.Services;
using SemanticAgentBroker.Core.Constants;
using SemanticAgentBroker.Core.Features.TaskState;
using SemanticAgentBroker.Core.Features.TaskState.Implementations;
using SemanticAgentBroker.Core.Features.TokenCounting;
using SemanticAgentBroker.Core.Services;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using System.Text.RegularExpressions;

namespace SemanticAgentBroker.Api.Features.ReAct.Services;

/// <summary>
/// Orchestrator for ReAct execution.
/// This engine uses the Subagent pattern. 
/// Each step gets a fresh context via ISubagentExecutor, preventing context overflow and token bloat. 
/// The orchestrator handles state persistence, tool execution, user approvals, and UI broadcasting.
/// </summary>
public class ReactExecutionEngine : IReactExecutionEngine
{
    private readonly AgentTaskStore _taskStore;
    private readonly TaskOperationsService _taskOps;
    private readonly ITaskStateManager _taskStateManager;
    private readonly ToolExecutionService _toolExecutionService;
    private readonly ISubagentExecutor _subagentExecutor;
    private readonly ITokenCounter _tokenCounter;
    private readonly AgentSystemOptions _sysOptions;
    private readonly AiProviderOptions _aiOptions;
    private readonly ILogger<ReactExecutionEngine> _logger;
    private readonly SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager? _lockManager;
    private readonly IServiceProvider? _serviceProvider;

    public ReactExecutionEngine(
        AgentTaskStore taskStore,
        TaskOperationsService taskOps,
        ITaskStateManager taskStateManager,
        ToolExecutionService toolExecutionService,
        ISubagentExecutor subagentExecutor,
        ITokenCounter tokenCounter,
        AgentSystemOptions sysOptions,
        AiProviderOptions aiOptions,
        ILogger<ReactExecutionEngine> logger,
        SemanticAgentBroker.Core.Features.Agents.IWorkspaceLockManager? lockManager = null,
        IServiceProvider? serviceProvider = null)
    {
        _taskStore = taskStore;
        _taskOps = taskOps;
        _taskStateManager = taskStateManager;
        _toolExecutionService = toolExecutionService;
        _subagentExecutor = subagentExecutor;
        _tokenCounter = tokenCounter;
        _sysOptions = sysOptions;
        _aiOptions = aiOptions;
        _logger = logger;
        _lockManager = lockManager;
        _serviceProvider = serviceProvider;
    }

    public async Task<string> ExecuteAsync(
        Guid sessionId,
        Kernel kernel,
        string systemPrompt,
        string originalObjective,
        bool debugMode = false,
        bool isContinuation = false,
        int? maxSteps = null,
        SemaphoreSlim? executionSemaphore = null,
        SemaphoreOwnershipState? semaphoreState = null,
        CancellationToken ct = default)
    {
        // Initialize state if needed (objective will be set if empty)
        await _taskStateManager.InitializeStateAsync(sessionId, originalObjective, ct);
        if (isContinuation)
        {
            await _taskStateManager.AppendObjectiveAsync(sessionId, originalObjective, ct);
        }

        await _taskStore.UpdateStateAsync(sessionId, TaskState.InProgress, "Orchestrator loop started");
        await _taskStore.BroadcastActivityAsync(sessionId, "Starting agent execution...");

        string finalResult = "";
        int startStep = isContinuation ? await _taskStateManager.GetMaxStepNumberAsync(sessionId, ct) : 0;
        int step = startStep;
        int maxStepsAllowed = maxSteps ?? int.MaxValue;
        int stepsExecutedInRun = 0;
        int messageSeq = await _taskStore.GetMaxSequenceNumberAsync(sessionId) + 1;
        int consecutivePlanUpdates = 0;  // Safety: detect infinite reasoning loops
        const int MaxConsecutivePlanUpdates = 5;

        while (stepsExecutedInRun < maxStepsAllowed)
        {
            step++;
            stepsExecutedInRun++;
            ct.ThrowIfCancellationRequested();

            // ── Check pause gate ──────────────────────────────────────────────
            if (executionSemaphore != null)
            {
                await ReleaseDuringWaitAsync(executionSemaphore, semaphoreState, sessionId, TaskState.Paused,
                    () => _taskOps.WaitIfPausedAsync(sessionId, ct), ct);
            }
            else
            {
                await _taskOps.WaitIfPausedAsync(sessionId, ct);
            }

            // ── Check for user interruptions ──────────────────────────────────
            var interruption = _taskOps.TryReadInterruption(sessionId);
            if (interruption != null)
            {
                _logger.LogInformation("User interruption received for {SessionId}: {Message}", sessionId, interruption.Message);
                consecutivePlanUpdates = 0;  // User intervention breaks any reasoning loop
                stepsExecutedInRun--;       // Exempt user interruption from consuming the agent's execution step quota
                await _taskStore.SaveChatMessageAsync(sessionId, messageSeq++, ChatRoleNames.User, $"[USER INTERRUPTION]: {interruption.Message}", userName: interruption.UserName);
                await _taskStore.LogEventAsync(sessionId, EventTypes.UserInterruption, LogLevelNames.User, interruption.Message);
                await _taskStore.BroadcastActivityAsync(sessionId, "Processing user feedback...");
                
                // Add interruption to the state manager so it's injected next loop
                await _taskStateManager.RecordStepAsync(sessionId, step, "UserInterruption", "", interruption.Message, ct);
                continue; // Skip LLM call and inject state again
            }

            await _taskStore.BroadcastActivityAsync(sessionId, "Agent is reasoning...");

            // ── Build Context Injection ───────────────────────────────────────
            int maxContextTokens = ModelRoutingHelper.ResolveMaxContextTokens(_aiOptions);
            string modelId = _aiOptions.ModelRouting.TryGetValue(TaskModelRole.PrimaryTextModel, out var route) ? route.ModelId : "default";

            var currentState = await _taskStateManager.ReadStateAsync(sessionId, ct);
            int compactionCount = currentState.CompactionCount;

            // Compute exact framing overhead from SubagentExecutor's
            // static template instead of using a hardcoded buffer, plus reserve budget for completion tokens.
            int systemPromptTokens = _tokenCounter.CountTokens(systemPrompt, modelId);
            int framingOverhead = _tokenCounter.CountTokens(SubagentExecutor.ReasoningFramingText, modelId);
            int outputReserve = Math.Min(_aiOptions.MaxTokensPerRequest, maxContextTokens / 2);
            int effectiveMaxStateTokens = Math.Max(500, maxContextTokens - systemPromptTokens - framingOverhead - outputReserve);

            var stateInjection = await _taskStateManager.BuildContextInjectionAsync(
                sessionId, step, systemPromptTokens + framingOverhead, effectiveMaxStateTokens, compactionCount, ct);

            int orchestratorTokens = systemPromptTokens + framingOverhead +
                _tokenCounter.CountTokens(stateInjection, modelId);

            await _taskStore.BroadcastTokenCountAsync(sessionId, orchestratorTokens, maxContextTokens, "Orchestrator");

            // ── Call Subagent ─────────────────────────────────────────────────
            var result = await _subagentExecutor.ExecuteStepAsync(
                sessionId, kernel, systemPrompt, stateInjection, step, maxContextTokens, debugMode, ct);

            if (result.IsError)
            {
                finalResult = $"Task failed: {result.ErrorMessage}";
                await _taskStateManager.FinalizeStateAsync(sessionId, finalResult, ct);
                await _taskStore.LogEventAsync(sessionId, EventTypes.ToolError, LogLevelNames.System, finalResult);
                break;
            }

            // Save LLM outputs for UI
            var dbAssistantResponse = "";
            var subagentName = $"Subagent {step}";
            if (!string.IsNullOrWhiteSpace(result.Thought)) dbAssistantResponse += $"{ReactFormatTokens.Thought} {result.Thought}\n";
            if (!string.IsNullOrWhiteSpace(result.Plan)) dbAssistantResponse += $"{ReactFormatTokens.Plan} {result.Plan}\n";
            
            if (result.Actions != null)
            {
                foreach (var toolAction in result.Actions)
                {
                    dbAssistantResponse += $"{ReactFormatTokens.Action} {toolAction.ActionName}\n";
                    dbAssistantResponse += $"{ReactFormatTokens.ActionInput} {toolAction.ActionInput}\n";
                }
            }

            if (!string.IsNullOrWhiteSpace(result.FinalAnswer)) dbAssistantResponse += $"{ReactFormatTokens.FinalAnswer} {result.FinalAnswer}\n";
            await _taskStore.SaveChatMessageAsync(sessionId, messageSeq++, ChatRoleNames.Assistant, dbAssistantResponse.TrimEnd(), subagentName);

            if (!string.IsNullOrEmpty(result.Thought))
            {
                // Note: BroadcastThoughtAsync is already called by SubagentExecutor during Phase 1
                // for immediate UI visibility. Here it is only persisted to the event log.
                await _taskStore.LogEventAsync(sessionId, EventTypes.Thought, LogLevelNames.Reasoning, result.Thought);
            }

            if (!string.IsNullOrEmpty(result.Plan))
            {
                // Persist runtime plan update to database and workspace only if plan content has changed
                var latestPlan = await _taskStore.GetLatestPlanAsync(sessionId);
                bool isPlanChanged = latestPlan == null || !string.Equals(latestPlan.PlanMarkdown?.Trim(), result.Plan.Trim(), StringComparison.Ordinal);

                if (isPlanChanged)
                {
                    await _taskStore.BroadcastPlanAsync(sessionId, result.Plan, requiresApproval: false);
                    await _taskStore.LogEventAsync(sessionId, EventTypes.PlanGenerated, LogLevelNames.Reasoning, result.Plan);

                    // Persist runtime plan update to database so history is preserved
                    try
                    {
                        int nextVersion = latestPlan != null ? latestPlan.Version + 1 : 1;
                        await _taskStore.SavePlanAsync(sessionId, nextVersion, result.Plan, null, PlanStatus.RuntimeUpdate);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to persist runtime plan update to database.");
                    }

                    // Persist runtime plan update to workspace so subsequent subagent steps see it,
                    // leaving the original approved implementation plan intact.
                    try
                    {
                        var workspacePath = SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(sessionId, _sysOptions);
                        Directory.CreateDirectory(workspacePath);
                        if (_lockManager != null)
                        {
                            using (await _lockManager.AcquireLockAsync(sessionId, "_runtime_plan.md", ct))
                            {
                                await File.WriteAllTextAsync(
                                    Path.Combine(workspacePath, "_runtime_plan.md"), result.Plan, ct);
                            }
                        }
                        else
                        {
                            await File.WriteAllTextAsync(
                                Path.Combine(workspacePath, "_runtime_plan.md"), result.Plan, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to persist runtime plan update to workspace.");
                        await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                            "[WARNING] Runtime plan was generated but could not be saved to _runtime_plan.md. " +
                            "The plan may be stale on the next step.");
                    }
                }
            }

            // ── Filter out "Action: None" responses ──────────────────────────
            var validActions = result.Actions?
                .Where(a => !a.ActionName.Equals(ReactFormatTokens.ActionNone, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // ── Handle Plan-Only or Inferred-Only Response (no explicit Action, no FinalAnswer) ─────────
            bool hasExplicitAction = validActions != null && validActions.Any(a => !a.IsInferred);

            if (!hasExplicitAction && string.IsNullOrEmpty(result.FinalAnswer))
            {
                consecutivePlanUpdates++;

                if (consecutivePlanUpdates >= MaxConsecutivePlanUpdates)
                {
                    // Safety: the model is stuck in a reasoning loop. Fail gracefully.
                    await _taskStore.LogEventAsync(sessionId, EventTypes.Log, LogLevelNames.System,
                        $"[ERROR] {consecutivePlanUpdates} consecutive steps without explicit tool execution. " +
                        "The model appears stuck. Terminating task.");
                    finalResult = "Task failed: the agent was unable to make progress after multiple reasoning attempts. " +
                                  "This can happen when the model repeatedly plans actions but fails to execute them. " +
                                  "Try rephrasing the request or simplifying the task.";
                    await _taskStateManager.FinalizeStateAsync(sessionId, finalResult, ct);
                    await _taskStore.LogEventAsync(sessionId, EventTypes.FinalAnswer, LogLevelNames.System, finalResult);
                    break;
                }

                if (consecutivePlanUpdates == 3)
                {
                    // Warning: inject a nudge into the next state injection
                    await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.Warning,
                        $"[WARNING] {consecutivePlanUpdates} consecutive steps without explicit tool execution. " +
                        "The model may be stuck in a reasoning loop.");
                    await _taskStateManager.RecordStepAsync(
                        sessionId, step, "PlanUpdate", "",
                        "[SYSTEM WARNING] You have reasoned for 3 consecutive steps without calling any tools explicitly. " +
                        "You MUST emit 'Action: <ToolName>' and 'ActionInput: <json>' in your next step or provide FinalAnswer. Stop planning and ACT.", ct);
                    continue;
                }

                if (validActions == null || validActions.Count == 0)
                {
                    // Record a synthetic step so state advances
                    await _taskStateManager.RecordStepAsync(
                        sessionId, step, "PlanUpdate", "",
                        "Plan generated/updated. Awaiting next action step.", ct);
                    continue;
                }
            }

            // Reset counter when a real, non-inferred action occurs
            if (hasExplicitAction)
            {
                consecutivePlanUpdates = 0;
            }

            // ── Check for FinalAnswer ─────────────────────────────────────────
            if (!string.IsNullOrEmpty(result.FinalAnswer))
            {
                finalResult = result.FinalAnswer;
                
                var anchored = await TryAnchorResultFromWorkspaceAsync(sessionId, finalResult, debugMode);
                if (anchored != null) finalResult = anchored;

                await _taskStateManager.FinalizeStateAsync(sessionId, finalResult, ct);
                await _taskStore.BroadcastWorkspaceFileChangedAsync(sessionId, "*", "Refresh");

                await _taskStore.LogEventAsync(sessionId, EventTypes.FinalAnswer, LogLevelNames.System, finalResult);
                break;
            }

            // ── Execute Actions (per-action step numbers, batch semaphore) ───────────────
            if (validActions != null && validActions.Count > 0)
            {
                int maxConcurrency = _aiOptions.Inference.MaxParallelToolCalls > 0 
                    ? _aiOptions.Inference.MaxParallelToolCalls : 3;

                var actionBatches = validActions.Chunk(maxConcurrency).ToList();

                foreach (var actionBatch in actionBatches)
                {
                    ct.ThrowIfCancellationRequested();
                    var actionsToRun = actionBatch.ToList();

                    var indexedActions = actionsToRun.Select((a, idx) => (Index: idx, Action: a)).ToList();

                    // Partition into safe and dangerous actions
                    var safeActions = indexedActions.Where(a => !ToolExecutionService.IsDangerousAction(kernel, a.Action.ActionName)).ToList();
                    var dangerousActions = indexedActions.Where(a => ToolExecutionService.IsDangerousAction(kernel, a.Action.ActionName)).ToList();
                    var rejectedResults = new List<(int Index, ToolAction Action, string Observation, string? FeedbackUserName)>();
                    var approvedDangerous = new List<(int Index, ToolAction Action)>();

                    // Handle ALL dangerous approvals serially with ONE semaphore release
                    if (dangerousActions.Count > 0)
                    {
                        bool semaphoreReleased = false;
                        if (executionSemaphore != null && (semaphoreState?.TryRelease() ?? true))
                        {
                            try
                            {
                                executionSemaphore.Release();
                                semaphoreReleased = true;
                            }
                            catch (ObjectDisposedException)
                            {
                                // Host shutdown may have already disposed the semaphore
                            }
                        }
                        bool approvalCompleted = false;
                        try
                        {
                            foreach (var da in dangerousActions)
                            {
                                var approvalDetail = $"[WARNING] DANGEROUS OPERATION: {da.Action.ActionName} requires approval\n\n**Parameters:**\n```\n{da.Action.ActionInput ?? "(none)"}\n```";
                                _taskOps.PrepareApprovalGate(sessionId);
                                await _taskStore.UpdateStateAsync(sessionId, TaskState.AwaitingApproval, approvalDetail);

                                bool approved = await _taskOps.WaitForApprovalAsync(sessionId, ct);

                                if (!approved)
                                {
                                    var stepFeedback = _taskOps.TryReadStepRejectionFeedback(sessionId);
                                    var feedback = !string.IsNullOrEmpty(stepFeedback?.Feedback) ? stepFeedback.Value.Feedback! : "User rejected the dangerous operation.";
                                    var feedbackUserName = !string.IsNullOrEmpty(stepFeedback?.UserName) ? stepFeedback.Value.UserName! : "Orchestrator";
                                    rejectedResults.Add((da.Index, da.Action, $"Rejected: {feedback}", feedbackUserName));
                                }
                                else
                                {
                                    approvedDangerous.Add(da);
                                }
                            }
                            approvalCompleted = true;
                        }
                        finally
                        {
                            if (executionSemaphore != null && semaphoreReleased)
                            {
                                try
                                {
                                    await executionSemaphore.WaitAsync(ct);
                                    semaphoreState?.TryAcquire();
                                }
                                catch (OperationCanceledException)
                                {
                                    // Shutdown or cancellation requested - IsHeld stays false
                                    // so the outer finally in AgentTaskQueueService won't double-release.
                                    if (semaphoreState != null) semaphoreState.IsHeld = false;
                                    if (approvalCompleted)
                                    {
                                        ct.ThrowIfCancellationRequested();
                                    }
                                }
                                catch (ObjectDisposedException ex)
                                {
                                    // Semaphore disposed during shutdown - IsHeld stays false so outer finally won't double-release.
                                    if (semaphoreState != null) semaphoreState.IsHeld = false;
                                    throw new OperationCanceledException("Execution semaphore was disposed during application shutdown.", ex, ct);
                                }
                            }
                        }
                        await _taskStore.UpdateStateAsync(sessionId, TaskState.InProgress, "Executing approved operations...");
                    }

                    // Execute ALL approved actions in parallel
                    var allApproved = safeActions.Concat(approvedDangerous).ToList();

                    var executionTasks = allApproved.Select(async item =>
                    {
                        ToolExecutionService toolService;
                        IServiceScope? toolScope = null;
                        if (_serviceProvider != null)
                        {
                            toolScope = _serviceProvider.CreateScope();
                            toolService = toolScope.ServiceProvider.GetRequiredService<ToolExecutionService>();
                        }
                        else
                        {
                            toolService = _toolExecutionService;
                        }

                        try
                        {
                            var observation = await toolService.ExecuteToolAsync(
                                sessionId, kernel, item.Action.ActionName, item.Action.ActionInput ?? "{}", step, debugMode, true, ct, subagentName);

                            return (Index: item.Index, Action: item.Action, Observation: observation, IsRejected: false, FeedbackUserName: (string?)null);
                        }
                        finally
                        {
                            toolScope?.Dispose();
                        }
                    }).ToList();

                    var executionResults = await Task.WhenAll(executionTasks);

                    var allBatchResults = rejectedResults
                        .Select(r => (Index: r.Index, Action: r.Action, Observation: r.Observation, IsRejected: true, FeedbackUserName: r.FeedbackUserName))
                        .Concat(executionResults)
                        .OrderBy(r => r.Index)
                        .ToList();

                    foreach (var res in allBatchResults)
                    {
                        if (res.IsRejected)
                        {
                            await _taskStore.SaveChatMessageAsync(sessionId, messageSeq++, ChatRoleNames.User, $"{ReactFormatTokens.Observation} [{res.Action.ActionName}] Dangerous operation rejected. Feedback: {res.Observation}", "Orchestrator", res.FeedbackUserName);
                            await _taskStateManager.RecordStepAsync(sessionId, step, res.Action.ActionName, res.Action.ActionInput, res.Observation, ct);
                        }
                        else
                        {
                            await _taskStore.SaveChatMessageAsync(sessionId, messageSeq++, ChatRoleNames.User, $"{ReactFormatTokens.Observation} [{res.Action.ActionName}] {res.Observation}", subagentName);
                            await _taskStateManager.RecordStepAsync(sessionId, step, res.Action.ActionName, res.Action.ActionInput, res.Observation, ct);
                        }
                    }
                }
                
                await _taskStateManager.UpdateFilesManifestAsync(sessionId, ct);
                await _taskStore.BroadcastWorkspaceFileChangedAsync(sessionId, "*", "Refresh");
            }
        }

        // Step limit exhaustion: if loop completed without FinalAnswer, fail gracefully
        if (string.IsNullOrWhiteSpace(finalResult))
        {
            finalResult = $"Task failed: Reached maximum step limit ({maxStepsAllowed}) without producing a FinalAnswer.";
            await _taskStateManager.FinalizeStateAsync(sessionId, finalResult, ct);
            await _taskStore.LogEventAsync(sessionId, EventTypes.FinalAnswer, LogLevelNames.System, finalResult);
        }

        // Truncate if necessary before returning, so it doesn't clutter UI chat
        int maxInline = _aiOptions.Inference.MaxFinalAnswerInlineChars;
        if (finalResult.Length > maxInline)
        {
            var workspacePath = GetWorkspacePath(sessionId);
            Directory.CreateDirectory(workspacePath);
            var filePath = Path.Combine(workspacePath, "final_result.md");
            if (_lockManager != null)
            {
                using (await _lockManager.AcquireLockAsync(sessionId, "final_result.md", ct))
                {
                    await File.WriteAllTextAsync(filePath, finalResult, ct);
                }
            }
            else
            {
                await File.WriteAllTextAsync(filePath, finalResult, ct);
            }
            await _taskStore.BroadcastWorkspaceFileChangedAsync(sessionId, "final_result.md", "Created");
            int safeCut = TaskStateManager.SafeTruncateIndex(finalResult, maxInline);
            finalResult = $"{finalResult[..safeCut]}\n\n... [TRUNCATED]\n\nThe full output was too large and has been saved to: /app/workdir/final_result.md";
        }

        return finalResult;
    }

    private async Task ReleaseDuringWaitAsync(
        SemaphoreSlim semaphore, SemaphoreOwnershipState? semaphoreState, Guid sessionId, TaskState waitState,
        Func<Task> waitAction, CancellationToken ct)
    {
        bool wasPaused = await _taskOps.IsPausedAsync(sessionId, ct);
        if (wasPaused)
        {
            while (await _taskOps.IsPausedAsync(sessionId, ct))
            {
                bool semaphoreReleased = false;
                if (semaphoreState?.TryRelease() ?? true)
                {
                    try
                    {
                        semaphore.Release();
                        semaphoreReleased = true;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Host shutdown may have already disposed the semaphore
                    }
                }
                bool waitCompleted = false;
                try
                {
                    await _taskStore.UpdateStateAsync(sessionId, waitState, "Task is paused by user.");
                    await _taskStore.BroadcastActivityAsync(sessionId, "Task is paused.");
                    await waitAction();
                    await _taskStore.UpdateStateAsync(sessionId, TaskState.Queued, "Task resumed. Waiting for execution slot...");
                    await _taskStore.BroadcastActivityAsync(sessionId, "Waiting for execution slot...");
                    waitCompleted = true;
                }
                finally
                {
                    // Guard re-acquire to prevent unhandled exception during shutdown or asymmetric re-acquisition
                    if (semaphoreReleased)
                    {
                        try
                        {
                            await semaphore.WaitAsync(ct);
                            semaphoreState?.TryAcquire();
                        }
                        catch (OperationCanceledException)
                        {
                            // Shutdown or cancellation requested - IsHeld stays false
                            // so the outer finally in AgentTaskQueueService won't double-release.
                            if (semaphoreState != null) semaphoreState.IsHeld = false;
                            if (waitCompleted)
                            {
                                ct.ThrowIfCancellationRequested();
                            }
                        }
                        catch (ObjectDisposedException ex)
                        {
                            // Semaphore disposed during shutdown - IsHeld stays false so outer finally won't double-release.
                            if (semaphoreState != null) semaphoreState.IsHeld = false;
                            throw new OperationCanceledException("Execution semaphore was disposed during application shutdown.", ex, ct);
                        }
                    }
                }
            }
            await _taskStore.UpdateStateAsync(sessionId, TaskState.InProgress, "Task resumed.");
            await _taskStore.BroadcastActivityAsync(sessionId, "Agent is reasoning...");
        }
    }

    private async Task<string?> TryAnchorResultFromWorkspaceAsync(Guid sessionId, string finalResult, bool debugMode)
    {
        var match = Regex.Match(finalResult, @"\[Read the final report here\]\((.*?)\)", RegexOptions.IgnoreCase);
        if (!match.Success && finalResult.Length < 500)
            match = Regex.Match(finalResult, @"/app/workdir/([\w\-\.]+\.(?:md|txt))", RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var rawTarget = match.Groups[1].Value.Trim().Trim('`', '"', '\'');
            var filename = Path.GetFileName(rawTarget);

            // Reject invalid filenames, path traversal, or internal state/plan/walkthrough files
            if (string.IsNullOrWhiteSpace(filename) || filename.StartsWith('_') || filename.Contains(".."))
                return null;

            var workspacePath = GetWorkspacePath(sessionId);
            var fullPath = Path.Combine(workspacePath, filename);

            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            if (!normalizedWorkspace.EndsWith(Path.DirectorySeparatorChar))
                normalizedWorkspace += Path.DirectorySeparatorChar;

            if (!Path.GetFullPath(fullPath).StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                return null;

            if (File.Exists(fullPath))
            {
                var content = await File.ReadAllTextAsync(fullPath);
                // Reject empty or whitespace-only file content
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                if (debugMode)
                    await _taskStore.LogEventAsync(sessionId, EventTypes.Debug, LogLevelNames.System,
                        $"[DEBUG] Anchored FinalAnswer to workspace file {filename} ({content.Length} chars)");
                return content;
            }
        }
        return null;
    }

    private string GetWorkspacePath(Guid sessionId) =>
        SemanticAgentBroker.Core.Features.Agents.Implementations.AgentTaskContext.GetWorkspacePath(sessionId, _sysOptions);
}
